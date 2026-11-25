// CoopPlus - local two-player co-op for GTA V story mode (Script Hook V .NET, v3 API).
// Player 2 plays on an Xbox-style controller; with TwoScreens.asi each player gets their own screen.
//
// Keys (keyboard, Player 1):  N = spawn / remove Player 2   K = split screen on/off
// Any controller:             Back + Start = join as Player 2 with that controller
// Player 2 controller:
//   Left stick move · Right stick camera · A (hold) sprint · X jump/climb · Y enter/exit car
//   (tap Y = drive, hold Y = passenger) · LT aim · RT shoot / punch · B reload
//   D-pad right/left or RB/LB = next/previous weapon · D-pad down = come to Player 1
//   D-pad up = camera distance · In a car: RT gas, LT brake/reverse, A handbrake, X change seat
//   Passenger: LT aim, RT shoot out the window

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

public partial class CoopCore
{
    // ------------------------------------------------------------------ settings
    Keys spawnKey = Keys.N, splitKey = Keys.K, swapKey = Keys.F9;
    int frameLag = 1;
    int padSetting = -1;                 // -1 = auto
    string p2ModelName = "auto";
    bool infiniteAmmo = true, shareWeapons = true, minimapFollowsP2 = true, invincible = false;
    int hudLag = 1, mapLag = 1;          // which tick's view a HUD draw / minimap lock lands on
    string driveMode = "direct";         // direct | tasks
    float stickDeadzone = 0.22f;

    static readonly string Dir = Path.Combine(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName), "CoopPlus");
    string logPath, cmdPath;

    // ------------------------------------------------------------------ state
    Ped p2; Blip p2Blip; Camera cam; Prop aimProp;
    int padSlot = -1;
    bool split;
    int view, prevView;                  // 1 = Player 1 frame, 2 = Player 2 frame
    float camYaw, camPitch = -8f, camDist = 4.2f, collDist = 4.2f; int camDistStep = 1; Vector3 smoothPivot; bool smoothReady;
    DateTime lastLookInput = DateTime.MinValue;
    Vector3 camPos, camFwd, aimPoint;
    string taskKind = ""; Vector3 taskDir; int taskTime;
    int yDownAt = -1; bool yHandled;
    int deadSince = -1;
    int lastShareTime, lastStateLog;
    string lastSeatNote = "";
    bool directDriveOk = true; int directProbeStart = -1; int probedVehicle, lastDrivenVehicle;

    Pad pad = new Pad(), prevPad = new Pad();
    Pad virtualPad; int virtualUntil;

    MemoryMappedFile splitMap, blockMap; MemoryMappedViewAccessor splitView, blockView; int blockSeq, blockBeat;
    XInput xinput; DirectPads dpads = new DirectPads();

    static readonly WeaponHash[] AllWeapons = (WeaponHash[])Enum.GetValues(typeof(WeaponHash));

    public CoopCore()
    {
        Directory.CreateDirectory(Dir);
        logPath = Path.Combine(Dir, "CoopPlus.log");
        cmdPath = Path.Combine(Dir, "cmd.txt");
        LoadSettings();
        Log("---- CoopPlus loaded");
        xinput = new XInput(Path.Combine(Dir, "xinput_p2.dll"), Log);
        try
        {
            splitMap = MemoryMappedFile.CreateOrOpen("GTASplitScreenCoop", 16);
            splitView = splitMap.CreateViewAccessor(0, 16);
            blockMap = MemoryMappedFile.CreateOrOpen("GTAXInputBlock", 20);
            blockView = blockMap.CreateViewAccessor(0, 20);
            WriteSplit(false);
            WriteBlock(-1);
        }
        catch (Exception e) { Log("shared memory error: " + e.Message); }
        Adopt();
    }

    void LoadSettings()
    {
        var ini = ScriptSettings.Load(Path.Combine(Dir, "CoopPlus.ini"));
        spawnKey = ini.GetValue("Keys", "SpawnPlayer2", Keys.N);
        splitKey = ini.GetValue("Keys", "SplitScreen", Keys.K);
        padSetting = ini.GetValue("Player2", "ControllerSlot", -1);
        p2ModelName = ini.GetValue("Player2", "Model", "auto");
        infiniteAmmo = ini.GetValue("Player2", "InfiniteAmmo", true);
        shareWeapons = ini.GetValue("Player2", "ShareWeaponsWithPlayer1", true);
        invincible = ini.GetValue("Player2", "Invincible", false);
        minimapFollowsP2 = ini.GetValue("Screen", "MinimapFollowsPlayer2", true);
        swapKey = ini.GetValue("Keys", "SwapScreens", Keys.F9);
        frameLag = ini.GetValue("Screen", "FrameLag", 1);
        friendlyFire = ini.GetValue("Player2", "FriendlyFire", true);
        hudLag = ini.GetValue("Screen", "HudFrameLag", 1);
        mapLag = ini.GetValue("Screen", "MinimapFrameLag", 1);
        driveMode = ini.GetValue("Player2", "DriveMode", "direct");
        stickDeadzone = ini.GetValue("Player2", "StickDeadzone", 0.22f);
        aimAssist = ini.GetValue("Player2", "AimAssist", true);
        layout = Math.Max(0, Math.Min(3, ini.GetValue("Screen", "Layout", 0)));
        menuKey = ini.GetValue("Keys", "Menu", Keys.F7);
        rideKey = ini.GetValue("Keys", "RideTogether", Keys.J);
        frameShare = Math.Max(0, Math.Min(2, ini.GetValue("Screen", "FrameShare", 0)));
    }

    void Log(string m)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("[HH:mm:ss] ") + m + Environment.NewLine); } catch { }
    }

    // ------------------------------------------------------------------ shared memory
    void WriteSplit(bool on)
    {
        if (splitView == null) return;
        int shown = frameLag == 1 ? prevView : view;  // view of the frame being presented (normally one tick behind)
        splitView.Write(4, shown == 2 ? 2 : 1);
        splitView.Write(8, Game.FrameCount);
        splitView.Write(12, layout);
        splitView.Write(0, on ? 1 : 0);
    }

    void WriteBlock(int slot)
    {
        if (blockView == null) return;
        if (slot >= DirectPads.SlotBase) slot = -1;     // GTA never sees PlayStation/Switch pads anyway
        blockSeq++; if ((blockSeq & 1) == 0) blockSeq++;      // odd = writing
        blockView.Write(16, blockSeq);
        blockView.Write(0, 0x58424C4B);
        blockView.Write(4, 2);
        blockView.Write(8, slot);
        blockView.Write(12, ++blockBeat);
        blockSeq++;                                            // even = done
        blockView.Write(16, blockSeq);
    }

    // ------------------------------------------------------------------ input
    void OnKeyDown(object s, KeyEventArgs e)
    {
        if (menu.Open && menu.Owner == 1) { menu.Key(e.KeyCode); return; }
        if (FeatureKey(e.KeyCode)) return;
        if (e.KeyCode == menuKey) { if (menu.Open) menu.Hide(); else { menu.Owner = 1; BuildMenu(); menu.Show(1); } return; }
        if (e.KeyCode == rideKey) { RideTogether(); return; }
        if (e.KeyCode == spawnKey) { if (Alive(p2)) Despawn("removed"); else Spawn(padSetting); }
        else if (e.KeyCode == splitKey) { split = !split; Notify(split ? "Split screen on" : "Split screen off"); }
        else if (e.KeyCode == swapKey) SwapScreens();
    }

    // F9: if Player 1 and Player 2 come out on the wrong screens, flip which frames go where (saved).
    // ------------------------------------------------------------------ settings menu (F7 / Player 2: Back)
    CoopMenu menu = new CoopMenu(); Pad lastRaw = new Pad();
    Keys menuKey = Keys.F7, rideKey = Keys.J;
    bool aimAssist = true;
    int layout;   // 0 two screens, 1 side by side, 2 top and bottom, 3 side by side (whole view)
    static readonly string[] LayoutNames = { "Two screens", "Side by side", "Top and bottom", "Side by side (whole)" };
    static readonly string[] Characters = { "auto", "player_zero", "player_one", "player_two", "ig_lamardavis", "ig_lestercrest", "a_m_y_hipster_01", "s_m_y_cop_01" };
    static readonly string[] CharacterNames = { "Auto", "Michael", "Franklin", "Trevor", "Lamar", "Lester", "Hipster", "Cop" };

    static string OnOff(bool b) { return b ? "On" : "Off"; }
    string PadName(int slot) { return slot < 0 ? "None" : slot >= DirectPads.SlotBase ? dpads.Describe(slot) + " " + (slot - DirectPads.SlotBase + 1) : "Xbox " + (slot + 1); }

    void Save(string section, string key, object value)
    {
        try
        {
            var ini = ScriptSettings.Load(Path.Combine(Dir, "CoopPlus.ini"));
            ini.SetValue(section, key, value.ToString()); ini.Save();
        }
        catch (Exception e) { Log("could not save setting: " + e.Message); }
    }

    void ApplyP2Flags()
    {
        if (!Alive(p2)) return;
        p2.IsInvincible = invincible;
        Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, p2, friendlyFire);
        Function.Call(Hash.SET_PED_CAN_BE_TARGETTED_BY_PLAYER, p2, Game.Player, friendlyFire);
        Function.Call(Hash.SET_CAN_ATTACK_FRIENDLY, p2, friendlyFire, false);
        Function.Call(Hash.SET_PED_INFINITE_AMMO_CLIP, p2, infiniteAmmo);
        SetupTeam(Game.Player.Character);
    }

    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Title = "Co-op"; menu.Sub = menu.Owner == 2 ? "PLAYER 2" : "SETTINGS";
        Action<string, Func<bool>, Action<bool>, string> toggle = (label, get, set, help) =>
            menu.Items.Add(new MenuItem { Label = label, Help = help, Value = () => OnOff(get()), Change = d => set(!get()) });
        if (Alive(p2))
        {
            toggle("Split screen", () => split, v => split = v, "Turn the split on or off");
            menu.Items.Add(new MenuItem
            {
                Label = "Screens", Help = "Two screens, or both players on one screen",
                Value = () => LayoutNames[layout],
                Change = d => { layout = (layout + (d < 0 ? 3 : 1)) % 4; Save("Screen", "Layout", layout); }
            });
            menu.Items.Add(new MenuItem { Label = "Swap screens", Help = "If each screen shows the wrong player", Change = d => SwapScreens() });
            menu.Items.Add(new MenuItem { Label = "Bring Player 2 here", Change = d => TeleportToP1(Game.Player.Character) });
        }
        if (Alive(p2)) menu.Items.Add(new MenuItem
        {
            Label = "Player 2 controller", Help = "Xbox controllers are 1-4; PlayStation and Switch controllers work too",
            Value = () => PadName(padSlot),
            Change = d =>
            {
                var all = new List<int>(); for (int i = 0; i < 4; i++) if (xinput.Connected(i)) all.Add(i); all.AddRange(dpads.ConnectedSlots());
                if (all.Count == 0) return;
                int k = all.IndexOf(padSlot); padSlot = all[((k < 0 ? 0 : k) + (d < 0 ? all.Count - 1 : 1)) % all.Count];
                WriteBlock(padSlot); Log("Player 2 controller -> " + PadName(padSlot));
            }
        });
        if (menu.Owner == 2 && Alive(p2)) { LoadMoney(); menu.Items.Insert(0, new MenuItem { Label = "Shop", Value = () => "$" + p2Money.ToString("N0"), Help = "Guns, armour, health and cars for Player 2", Change = d => BuildShopMenu() }); }
        menu.Items.Add(new MenuItem { Label = "Activities", Help = "Deathmatch, survival, race", Change = d => BuildActivitiesMenu() });
        if (Alive(p2))
        {
            menu.Items.Add(new MenuItem { Label = "Identify screens", Help = "Shows PLAYER 1 / PLAYER 2 on each screen", Change = d => identifyUntil = Game.GameTime + 4000 });
            menu.Items.Add(new MenuItem { Label = "Frame share", Help = "Who gets more frames per second", Value = () => FrameShareNames[frameShare], Change = d => { frameShare = (frameShare + (d < 0 ? 2 : 1)) % 3; Save("Screen", "FrameShare", frameShare); } });
            menu.Items.Add(new MenuItem { Label = "First person (Player 2)", Value = () => OnOff(firstPerson), Help = "Also: click the right stick", Change = d => firstPerson = !firstPerson });
            menu.Items.Add(new MenuItem { Label = "New outfit (Player 2)", Change = d => Function.Call(Hash.SET_PED_RANDOM_COMPONENT_VARIATION, p2, 0) });
            menu.Items.Add(new MenuItem { Label = "Deliver Player 2's car", Help = "The last car Player 2 drove", Change = d => DeliverMyCar() });
        }
        toggle("Friendly fire", () => friendlyFire, v => { friendlyFire = v; Save("Player2", "FriendlyFire", v); ApplyP2Flags(); }, "You can shoot each other");
        toggle("Player 2 can't die", () => invincible, v => { invincible = v; Save("Player2", "Invincible", v); ApplyP2Flags(); }, null);
        toggle("Infinite ammo", () => infiniteAmmo, v => { infiniteAmmo = v; Save("Player2", "InfiniteAmmo", v); ApplyP2Flags(); }, "For Player 2");
        toggle("Share guns", () => shareWeapons, v => { shareWeapons = v; Save("Player2", "ShareWeaponsWithPlayer1", v); }, "Player 2 gets every gun Player 1 picks up");
        toggle("Aim assist", () => aimAssist, v => { aimAssist = v; Save("Player2", "AimAssist", v); }, "Player 2's aim snaps to targets");
        toggle("Player 2 minimap", () => minimapFollowsP2, v => { minimapFollowsP2 = v; Save("Screen", "MinimapFollowsPlayer2", v); }, "TV minimap centred on Player 2");
        menu.Items.Add(new MenuItem
        {
            Label = "Driving", Help = "Smooth = analogue, Classic = GTA driving actions",
            Value = () => driveMode == "direct" ? "Smooth" : "Classic",
            Change = d => { driveMode = driveMode == "direct" ? "tasks" : "direct"; directDriveOk = true; Save("Player2", "DriveMode", driveMode); }
        });
        menu.Items.Add(new MenuItem
        {
            Label = "Player 2 character", Help = "Respawns Player 2 as this character",
            Value = () => CharacterNames[Math.Max(0, Array.IndexOf(Characters, p2ModelName))],
            Change = d =>
            {
                int i = Array.IndexOf(Characters, p2ModelName); if (i < 0) i = 0;
                p2ModelName = Characters[(i + (d < 0 ? Characters.Length - 1 : 1)) % Characters.Length];
                Save("Player2", "Model", p2ModelName);
                if (Alive(p2)) { int slot = padSlot; bool was = split; Despawn("changing character"); Spawn(slot); split = was; }
            }
        });
        menu.Items.Add(new MenuItem
        {
            Label = Alive(p2) ? "Remove Player 2" : "Add Player 2",
            Change = d => { if (Alive(p2)) Despawn("removed"); else Spawn(padSetting); menu.Hide(); }
        });
    }

    void SwapScreens()
    {
        frameLag ^= 1; hudLag ^= 1; mapLag ^= 1;
        try
        {
            var ini = ScriptSettings.Load(Path.Combine(Dir, "CoopPlus.ini"));
            ini.SetValue("Screen", "FrameLag", frameLag); ini.SetValue("Screen", "HudFrameLag", hudLag); ini.SetValue("Screen", "MinimapFrameLag", mapLag);
            ini.Save();
        }
        catch (Exception ex) { Log("could not save swap: " + ex.Message); }
        Log("screens swapped: FrameLag " + frameLag);
        Notify("Screens swapped");
    }

    void ReadPad()
    {
        prevPad = pad;
        if (virtualPad != null && Game.GameTime < virtualUntil) { pad = virtualPad; return; }
        virtualPad = null;
        pad = padSlot >= DirectPads.SlotBase ? dpads.Read(padSlot, stickDeadzone) : padSlot >= 0 ? xinput.Read(padSlot, stickDeadzone) : new Pad();
    }

    bool Pressed(int b) { return (pad.Buttons & b) != 0 && (prevPad.Buttons & b) == 0; }
    bool Held(int b) { return (pad.Buttons & b) != 0; }

    int AutoSlot()
    {
        for (int i = 3; i >= 0; i--) if (xinput.Connected(i)) return i;   // highest slot = the newest controller
        var ds = dpads.ConnectedSlots(); return ds.Count > 0 ? ds[0] : -1;   // else a PlayStation / Switch controller
    }

    // ------------------------------------------------------------------ spawn / despawn
    void Spawn(int slot)
    {
        Ped p1 = Game.Player.Character;
        if (!Alive(p1)) return;
        padSlot = slot >= 0 ? slot : AutoSlot();
        if (padSlot < 0) { Notify("Plug in a controller for Player 2 first"); return; }

        Model model = new Model(ChooseModel(p1));
        if (!model.Request(5000)) { Notify("Could not load Player 2's character"); return; }
        Vector3 pos = p1.GetOffsetPosition(new Vector3(1.6f, 0.4f, 0f));
        p2 = World.CreatePed(model, pos, p1.Heading);
        model.MarkAsNoLongerNeeded();
        if (!Alive(p2)) { Notify("Player 2 could not spawn - try again"); return; }

        p2.IsPersistent = true;
        Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, p2, true, true);
        lastP1Handle = p1.Handle; lastP1Pos = p1.Position; hidden = false; respawnAt = 0;
        p2.BlockPermanentEvents = true;
        SetupTeam(p1);
        p2.CanBeDraggedOutOfVehicle = false;
        p2.Accuracy = 100;
        p2.MaxHealth = 300; p2.Health = 300; p2.Armor = 100;
        p2.IsInvincible = invincible;
        Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, p2);
        Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, p2, 0, false);
        Function.Call(Hash.SET_PED_CONFIG_FLAG, p2, 184, true);   // don't auto-shuffle into the driver seat
        Function.Call(Hash.SET_PED_CONFIG_FLAG, p2, 32, false);   // don't fly through the windscreen
        Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, p2, friendlyFire);
        Function.Call(Hash.SET_PED_CAN_BE_TARGETTED_BY_PLAYER, p2, Game.Player, friendlyFire);
        Function.Call(Hash.SET_CAN_ATTACK_FRIENDLY, p2, friendlyFire, false);
        Function.Call(Hash.SET_ENTITY_CAN_BE_DAMAGED, p2, true);
        if (infiniteAmmo) Function.Call(Hash.SET_PED_INFINITE_AMMO_CLIP, p2, true);
        p2.Weapons.Give(WeaponHash.Pistol, 250, false, true);
        ShareWeapons(true);
        RestoreLoadout();

        p2Blip = p2.AddBlip();
        p2Blip.Sprite = BlipSprite.Standard;
        p2Blip.Color = BlipColor.Blue;
        p2Blip.Name = "Player 2";
        p2Blip.Scale = 0.9f;
        Function.Call(Hash.SHOW_HEADING_INDICATOR_ON_BLIP, p2Blip, true);

        if (cam == null || !cam.Exists())
            cam = World.CreateCamera(pos, Vector3.Zero, 60f);
        camYaw = p1.Heading; camPitch = -8f;
        if (aimProp == null || !aimProp.Exists())
        {
            aimProp = World.CreateProp(new Model("prop_golf_ball"), pos, false, false);
            if (aimProp != null)
            {
                aimProp.IsVisible = false; aimProp.IsCollisionEnabled = false; aimProp.IsPositionFrozen = true;
                aimProp.IsPersistent = true;
            }
        }
        WriteBlock(padSlot);
        split = true;
        deadSince = -1; taskKind = "";
        Log("Player 2 spawned, controller slot " + padSlot + ", model " + p2.Model.Hash.ToString("X"));
        Notify("Player 2 joined (controller " + (padSlot + 1) + ")");
    }

    string ChooseModel(Ped p1)
    {
        if (p2ModelName != "auto") return p2ModelName;
        int h = p1.Model.Hash;
        if (h == Game.GenerateHash("player_one")) return "player_zero";   // Franklin -> Michael
        return "player_one";                                               // Michael/Trevor -> Franklin
    }

    void Despawn(string why)
    {
        SaveLoadout();
        if (p2Blip != null && p2Blip.Exists()) p2Blip.Delete();
        if (p2 != null && p2.Exists()) p2.Delete();
        p2 = null; p2Blip = null;
        split = false; respawnAt = 0; hidden = false;
        ReleaseHeld();
        WriteBlock(-1);
        RenderP2(false);
        UnlockMap();
        Log("Player 2 " + why);
        Notify("Player 2 " + why);
    }

    // ---- called by the loader (CoopPlusLoader.cs)
    public void Tick() { OnTick(null, EventArgs.Empty); }
    public void Key(KeyEventArgs e) { OnKeyDown(null, e); }
    public void Unload(bool reloading)
    {
        if (reloading && Alive(p2))
        {
            try { File.WriteAllText(Path.Combine(Dir, "handoff.txt"), p2.Handle + " " + padSlot + " " + (split ? 1 : 0)); } catch { }
            keepP2 = true;
        }
        Cleanup();
    }
    bool keepP2;

    // a reloaded core takes over the Player 2 the previous core left behind
    void Adopt()
    {
        string h = Path.Combine(Dir, "handoff.txt");
        if (!File.Exists(h)) return;
        try
        {
            if ((DateTime.Now - File.GetLastWriteTime(h)).TotalSeconds < 15)
            {
                string[] a = File.ReadAllText(h).Split(' ');
                Ped old = Entity.FromHandle(int.Parse(a[0])) as Ped;
                if (Alive(old))
                {
                    p2 = old; padSlot = int.Parse(a[1]); split = a[2] == "1";
                    Ped p1 = Game.Player.Character;
                    SetupTeam(p1); lastP1Handle = p1.Handle; lastP1Pos = p1.Position;
                    p2Blip = p2.AddBlip(); p2Blip.Color = BlipColor.Blue; p2Blip.Name = "Player 2"; p2Blip.Scale = 0.9f;
                    Function.Call(Hash.SHOW_HEADING_INDICATOR_ON_BLIP, p2Blip, true);
                    cam = World.CreateCamera(p2.Position, Vector3.Zero, 60f); camYaw = p2.Heading;
                    aimProp = World.CreateProp(new Model("prop_golf_ball"), p2.Position, false, false);
                    if (aimProp != null) { aimProp.IsVisible = false; aimProp.IsCollisionEnabled = false; aimProp.IsPositionFrozen = true; aimProp.IsPersistent = true; }
                    Log("reloaded - kept Player 2 (slot " + padSlot + ")");
                }
            }
        }
        catch (Exception e) { Log("handoff failed: " + e.Message); }
        try { File.Delete(h); } catch { }
    }

    void Cleanup()
    {
        try
        {
            WriteSplit(false); WriteBlock(-1);
            RenderP2(false); UnlockMap(); ReleaseHeld();
            if (p2Blip != null && p2Blip.Exists()) p2Blip.Delete();
            if (p2 != null && p2.Exists() && !keepP2) p2.Delete();
            if (aimProp != null && aimProp.Exists()) aimProp.Delete();
            if (cam != null && cam.Exists()) cam.Delete();
        }
        catch { }
    }

    static bool Alive(Ped p) { return p != null && p.Exists() && !p.IsDead; }
    static void Notify(string m) { GTA.UI.Notification.Show(m); }

    // ------------------------------------------------------------------ main loop
    void OnTick(object s, EventArgs e)
    {
        try { Step(); }
        catch (Exception ex) { Log("ERROR " + ex); }
    }

    void Step()
    {
        if (pendingCmds.Count > 0 || Game.FrameCount % 15 == 0) RunCommands();
        ReadPad();
        // Player 2's menu: Back opens it; while it's open the pad drives the menu, not the character
        Pad raw = pad, rawPrev = lastRaw; lastRaw = raw;
        if (menu.Open && menu.Owner == 2) { menu.PadInput(raw, rawPrev); pad = new Pad(); prevPad = new Pad(); }
        else if (Alive(p2) && (raw.Buttons & Pad.Back) != 0 && (rawPrev.Buttons & Pad.Back) == 0 && (raw.Buttons & Pad.Start) == 0)
        { menu.Owner = 2; BuildMenu(); menu.Show(2); pad = new Pad(); prevPad = new Pad(); }
        if (!(menu.Open && menu.Owner == 2) && WeaponWheelInput(raw, rawPrev)) { pad = new Pad(); prevPad = new Pad(); }
        if (menu.Open && menu.Owner == 1)
        {
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, 1, true); Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, 2, true);
        }
        Ped p1 = Game.Player.Character;

        // join by Back + Start on any controller
        if (!Alive(p2) && deadSince < 0)
        {
            for (int i = 0; i < 4; i++)
            {
                Pad q = xinput.Read(i, 0.3f);
                if ((q.Buttons & Pad.Back) != 0 && (q.Buttons & Pad.Start) != 0) { Spawn(i); break; }
            }
            if (Game.FrameCount % 10 == 0)
                foreach (int ds in dpads.ConnectedSlots())
                {
                    Pad q = dpads.Read(ds, 0.3f);
                    if ((q.Buttons & Pad.Back) != 0 && (q.Buttons & Pad.Start) != 0) { Spawn(ds); break; }   // Share + Options
                }
        }

        // a mission script deleted Player 2: bring them back next to Player 1
        if (respawnAt > 0 && Game.GameTime > respawnAt && Alive(p1) && !SceneBlocking()) { respawnAt = 0; Spawn(padSlot); }
        if (p2 == null) { WriteSplit(false); if (menu.Open) menu.Draw(); return; }
        if (!p2.Exists())
        {
            Log("Player 2 was removed by the game/mission - respawning");
            p2 = null; WriteSplit(false); RenderP2(false); UnlockMap();
            if (p2Blip != null && p2Blip.Exists()) p2Blip.Delete();
            respawnAt = Game.GameTime + 2500; return;
        }

        if (p2.IsDead) { HandleDeath(p1); return; }
        if (!MissionSafety(p1)) return;
        if (p2Blip != null && p2Blip.Exists()) p2Blip.Rotation = (int)p2.Heading;

        WriteBlock(padSlot);                     // keep the "hide Player 2's controller from GTA" signal alive
        if (Game.GameTime - lastKeepTick > 500) KeepWorldAroundBothPlayers(p1);
        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, p2.Position.X, p2.Position.Y, p2.Position.Z);
        if (Game.GameTime - lastRelSync > 1000) SyncRelationships(p1);
        UpdateFeatures(p1);
        UpdateFeatures2(p1);
        if (shareWeapons && Game.GameTime - lastShareTime > 2000) ShareWeapons(false);
        if (Game.GameTime - lastPickupScan > 150) CollectPickups();
        RedirectPlayer1IntoMyCar(p1);

        int hpNow = p2.Health + p2.Armor;
        if (hpNow < lastHp - 2) { hitFlashUntil = Game.GameTime + 220; Rumble(Math.Min(1f, (lastHp - hpNow) / 40f + 0.3f), 220); }
        lastHp = hpNow;

        UpdateCamera();
        if (p2.IsInVehicle()) VehicleControls(); else OnFootControls(p1);
        SplitFrame(p1, false);
    }

    int lastHp, hitFlashUntil;

    // ---- frame alternation for split screen (also used while Player 2 is down, for the WASTED screen)
    void SplitFrame(Ped p1, bool down)
    {
        bool canSplit = split && Alive(p1) && !Game.IsPaused && !Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING)
                        && !Function.Call<bool>(Hash.IS_PLAYER_SWITCH_IN_PROGRESS) && cam != null && cam.Exists();
        prevView = view;
        view = canSplit ? ViewFor(Game.FrameCount) : 1;
        RenderP2(view == 2);
        WriteSplit(canSplit);

        int hudView = hudLag == 0 ? view : prevView;
        int mapView = mapLag == 0 ? view : prevView;
        if (canSplit && minimapFollowsP2 && mapView == 2) LockMapToP2(); else UnlockMap();
        if (canSplit && hudView == 2)
        {
            if (down) DrawWastedNative(); else { DrawP2Hud(); DrawP2Money(); DrawP2Wanted(); }
            DrawIdentify(2);
            if (wheelOpen) wheel.Draw();
            if (Game.GameTime < hitFlashUntil) Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1f, 1f, 200, 0, 0, 70, false);
        }
        if (canSplit && hudView == 2) Function.Call(Hash.HIDE_HUD_COMPONENT_THIS_FRAME, 1);   // GTA's real stars stay on Player 1's screen; hidden on Player 2's frames
        if (canSplit && hudView == 1) { DrawP1Hud(); DrawReviveHint(); DrawIdentify(1); }
        if (!canSplit) DrawReviveHint();
        DrawActivityHud(); DrawBig();
        if (menu.Open && (!canSplit || hudView == menu.Owner)) menu.Draw();     // only on its owner's screen
        if (!canSplit && !down) DrawP2HudSmall();
        if (canSplit) DrawFrameTag(hudView);      // last, so no overlay can tint it

        if (Game.GameTime - lastStateLog > 2000 && File.Exists(Path.Combine(Dir, "debug.on"))) { lastStateLog = Game.GameTime; LogState(); }
    }

    // ------------------------------------------------------------------ pickups for Player 2
    // GTA only lets the real player collect pickups. When Player 2 walks over one, give it to them.
    int lastPickupScan;
    Dictionary<int, WeaponHash> weaponByModel;
    static readonly int HealthModel = Game.GenerateHash("prop_ld_health_pack"), ArmourModel = Game.GenerateHash("prop_armour_pickup"),
                        ArmourModel2 = Game.GenerateHash("prop_bodyarmour_02"), SnackModel = Game.GenerateHash("prop_choc_ego"),
                        CashModel1 = Game.GenerateHash("prop_cash_pile_01"), CashModel2 = Game.GenerateHash("prop_cash_pile_02"), CashModel3 = Game.GenerateHash("prop_money_bag_01");

    void CollectPickups()
    {
        lastPickupScan = Game.GameTime;
        if (p2.IsInVehicle()) return;
        if (weaponByModel == null)
        {
            weaponByModel = new Dictionary<int, WeaponHash>();
            foreach (WeaponHash h in AllWeapons)
            {
                int m = Function.Call<int>(Hash.GET_WEAPONTYPE_MODEL, (uint)h);
                if (m != 0 && !weaponByModel.ContainsKey(m)) weaponByModel[m] = h;
            }
        }
        foreach (Prop pk in World.GetNearbyPickupObjects(p2.Position, 1.6f))
        {
            if (pk == null || !pk.Exists()) continue;
            if (Math.Abs(pk.Position.Z - p2.Position.Z) > 1.8f) continue;
            int model = pk.Model.Hash; string what = null;
            WeaponHash w;
            if (model == HealthModel || model == SnackModel)
            {
                if (p2.Health >= p2.MaxHealth) continue;
                p2.Health = Math.Min(p2.MaxHealth, p2.Health + 100); what = "Health";
            }
            else if (model == ArmourModel || model == ArmourModel2)
            {
                if (p2.Armor >= 100) continue;
                p2.Armor = 100; what = "Armour";
            }
            else if (weaponByModel.TryGetValue(model, out w))
            {
                p2.Weapons.Give(w, 250, true, true); what = WeaponLabel(w);
            }
            else if (model == CashModel1 || model == CashModel2 || model == CashModel3) { AddMoney(100 + pk.Handle % 5 * 50, "cash pickup"); what = "Cash"; }
            else continue;   // mission items stay for Player 1
            pk.Delete();
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
            pickupToast = what; pickupToastUntil = Game.GameTime + 2000;
            Log("Player 2 picked up " + what);
        }
    }
    string pickupToast = ""; int pickupToastUntil;

    // ------------------------------------------------------------------ keep the world around both players
    // GTA deletes cars and people it thinks nobody can see, judging by whichever camera drew the last
    // frame. With two cameras taking turns, things in front of one player get deleted because the
    // other camera can't see them. Anything close to either player is held ("in use") and released
    // again once both players have moved away.
    int lastKeepTick;
    Dictionary<int, Entity> held = new Dictionary<int, Entity>();
    const float HoldRadius = 60f, ReleaseRadius = 90f; const int MaxHeld = 70;

    void KeepWorldAroundBothPlayers(Ped p1)
    {
        lastKeepTick = Game.GameTime;
        if (!Alive(p1) || !Alive(p2)) return;
        Vector3 a = p1.Position, b = p2.Position;

        // release what's now far from both players (or gone)
        List<int> drop = new List<int>();
        foreach (var kv in held)
        {
            Entity e = kv.Value;
            if (e == null || !e.Exists()) { drop.Add(kv.Key); continue; }
            if (e.Position.DistanceTo(a) > ReleaseRadius && e.Position.DistanceTo(b) > ReleaseRadius) { e.MarkAsNoLongerNeeded(); drop.Add(kv.Key); }
        }
        foreach (int k in drop) held.Remove(k);
        if (held.Count >= MaxHeld) return;

        // hold what's close to either player, nearest first (only things the game/missions don't already own)
        IEnumerable<Entity> near = World.GetNearbyVehicles(a, HoldRadius).Cast<Entity>()
            .Concat(World.GetNearbyVehicles(b, HoldRadius))
            .Concat(World.GetNearbyPeds(a, HoldRadius * 0.6f)).Concat(World.GetNearbyPeds(b, HoldRadius * 0.6f))
            .Where(e => e != null && e.Exists() && e != p1 && e != p2 && !held.ContainsKey(e.Handle))
            .OrderBy(e => Math.Min(e.Position.DistanceTo(a), e.Position.DistanceTo(b)));
        foreach (Entity e in near)
        {
            if (held.Count >= MaxHeld) break;
            if (Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, e)) continue;     // a mission's own thing: leave it
            Ped asPed = e as Ped;
            if (asPed != null && (asPed.IsDead || asPed.IsPlayer)) continue;
            e.IsPersistent = true;
            held[e.Handle] = e;
        }
    }

    void ReleaseHeld()
    {
        foreach (var kv in held) { try { if (kv.Value != null && kv.Value.Exists()) kv.Value.MarkAsNoLongerNeeded(); } catch { } }
        held.Clear();
    }

    // ------------------------------------------------------------------ teams
    // Player 2 has their own team so the two of you can shoot each other (friendly fire).
    // Every second, whoever likes or hates Player 1 is made to feel the same about Player 2.
    int p2Group, lastRelSync;
    bool friendlyFire = true;
    HashSet<int> mirrored = new HashSet<int>();

    void SetupTeam(Ped p1)
    {
        if (p2Group == 0)
        {
            OutputArgument g = new OutputArgument();
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "COOPPLUS_P2", g);
            p2Group = g.GetResult<int>();
        }
        int pg = p1.RelationshipGroup.Hash;
        int rel = friendlyFire ? 1 : 0;            // 1 = respect (won't auto-attack, can still be shot), 0 = companion
        Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, rel, p2Group, pg);
        Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, rel, pg, p2Group);
        Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, p2, p2Group);
        mirrored.Clear();
    }

    void SyncRelationships(Ped p1)
    {
        lastRelSync = Game.GameTime;
        if (p2Group == 0 || !Alive(p1)) return;
        int pg = p1.RelationshipGroup.Hash;
        HashSet<int> groups = new HashSet<int>();
        foreach (Ped q in World.GetNearbyPeds(p1.Position, 200f))
        {
            if (q == p1 || q == p2 || !q.Exists()) continue;
            int g = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, q);
            if (g != pg && g != p2Group) groups.Add(g);
        }
        foreach (int g in groups)
        {
            int toP1 = Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_GROUPS, g, pg);
            int fromP1 = Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_GROUPS, pg, g);
            if (toP1 > 5) toP1 = 3; if (fromP1 > 5) fromP1 = 3;   // 255 = "no relationship" -> neutral
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, toP1, g, p2Group);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, fromP1, p2Group, g);
            if (mirrored.Add(g)) Log("team sync: group " + g.ToString("X") + " -> P1 " + toP1);
        }
    }

    // ------------------------------------------------------------------ story missions / cutscenes
    int respawnAt; bool hidden; int lastP1Handle; Vector3 lastP1Pos; int catchUpAt;

    static bool SceneBlocking()
    {
        return Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING) || Function.Call<bool>(Hash.IS_PLAYER_SWITCH_IN_PROGRESS)
            || Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT) || Function.Call<bool>(Hash.IS_SCREEN_FADING_OUT)
            || Function.Call<bool>(Hash.GET_IS_LOADING_SCREEN_ACTIVE);
    }

    // Returns false when Player 2 should sit this frame out.
    bool MissionSafety(Ped p1)
    {
        if (SceneBlocking() || !Alive(p1))
        {
            if (!hidden)
            {
                hidden = true;
                p2.IsVisible = false; p2.IsCollisionEnabled = false; p2.IsPositionFrozen = true; p2.IsInvincible = true;
                if (p2Blip != null && p2Blip.Exists()) p2Blip.Alpha = 0;
                Log("cutscene/switch/fade - Player 2 hidden");
            }
            RenderP2(false); UnlockMap(); WriteSplit(false);
            return false;
        }
        if (hidden)
        {
            hidden = false;
            p2.IsVisible = true; p2.IsCollisionEnabled = true; p2.IsPositionFrozen = false; p2.IsInvincible = invincible;
            if (p2Blip != null && p2Blip.Exists()) p2Blip.Alpha = 255;
            if (p2.Position.DistanceTo(p1.Position) > 20f || (p1.IsInVehicle() && !p2.IsInVehicle())) TeleportToP1(p1);
            Log("scene over - Player 2 back");
        }
        // switched character: stay on the new character's side
        if (p1.Handle != lastP1Handle)
        {
            if (lastP1Handle != 0)
            {
                lastP1Handle = p1.Handle;
                Log("Player 1 switched character");
                // Player 2 is always the main character Player 1 isn't playing
                if (p2ModelName == "auto" && p2.Model.Hash != Game.GenerateHash(ChooseModel(p1)))
                {
                    int slot = padSlot;
                    if (p2Blip != null && p2Blip.Exists()) p2Blip.Delete();
                    p2.Delete(); p2 = null;
                    Spawn(slot);
                    return false;
                }
                SetupTeam(p1); TeleportToP1(p1);
            }
            lastP1Handle = p1.Handle;
        }
        // the mission moved Player 1 far away in one go: follow
        if (lastP1Pos != Vector3.Zero && p1.Position.DistanceTo(lastP1Pos) > 60f && catchUpAt == 0) catchUpAt = Game.GameTime + 800;
        lastP1Pos = p1.Position;
        if (catchUpAt > 0 && Game.GameTime > catchUpAt) { catchUpAt = 0; TeleportToP1(p1); Log("Player 1 was moved by the game - Player 2 followed"); }
        // the mission took Player 1's controls (scripted moment): Player 2 waits too
        if (!Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player))
        {
            if (taskKind.StartsWith("move") || taskKind.StartsWith("aim") || taskKind == "shoot") { Function.Call(Hash.CLEAR_PED_TASKS, p2); taskKind = ""; }
            pad = new Pad();
        }
        // keep Player 2 protected from mission "clear the area" calls
        if (!Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, p2)) Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, p2, true, true);
        return true;
    }

    void DrawWasted()
    {
        Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1f, 1f, 0, 0, 0, 110, false);
        DrawText("WASTED", 0.5f, 0.42f, 1.6f, Alignment.Center);
        int left = Math.Max(0, 5 - (Game.GameTime - deadSince) / 1000);
        DrawText("Back next to Player 1 in " + left, 0.5f, 0.55f, 0.5f, Alignment.Center);
    }

    void HandleDeath(Ped p1)
    {
        if (deadSince < 0) { deadSince = Game.GameTime; Log("Player 2 died"); StartWasted(); P2Died(); }
        UpdateFeatures(p1);
        // slowly circle the body while the TV shows WASTED
        if (cam != null && cam.Exists())
        {
            camYaw += 12f * Math.Min(Game.LastFrameTime, 0.05f);
            Vector3 f = Forward(camYaw, -25f);
            camPos = p2.Position + new Vector3(0, 0, 0.5f) - f * 4.5f;
            cam.Position = camPos; cam.Rotation = new Vector3(-25f, 0f, camYaw);
        }
        SplitFrame(p1, true);
        int respawnDelay = Alive(p1) && p1.Position.DistanceTo(p2.Position) < 40f ? ReviveWindowMs : 5000;   // close by: time to revive
        if (deadSince < 0 || Game.GameTime - deadSince <= respawnDelay) return;
        if (deadSince < 0) { deadSince = Game.GameTime; RenderP2(false); UnlockMap(); WriteSplit(false); }
        if (Game.GameTime - deadSince > 5000)
        {
            int slot = padSlot;
            if (p2Blip != null && p2Blip.Exists()) p2Blip.Delete();
            p2.MarkAsNoLongerNeeded();
            p2 = null; deadSince = -1;
            Spawn(slot);
        }
        else WriteSplit(false);
    }

    // ------------------------------------------------------------------ camera
    void UpdateCamera()
    {
        if (cam == null || !cam.Exists()) return;
        float dt = Math.Min(Game.LastFrameTime, 0.05f);
        bool aiming = pad.LT > 0.35f && !p2.IsInVehicle();
        float look = aiming ? 110f : 190f;
        if (Math.Abs(pad.RX) > 0.01f || Math.Abs(pad.RY) > 0.01f) lastLookInput = DateTime.Now;
        camYaw -= pad.RX * look * dt;
        camPitch += pad.RY * look * 0.7f * dt;
        camPitch = Clamp(camPitch, -70f, 60f);
        if (Pressed(Pad.DUp)) { camDistStep = (camDistStep + 1) % 3; }
        bool armedNow = IsGun(p2.Weapons.Current.Hash);
        if (aiming && armedNow && aimAssist) AimAssist(dt, !wasAiming);
        wasAiming = aiming && armedNow;

        Vector3 pivot; float dist; float side = 0f;
        Vehicle v = p2.CurrentVehicle;
        if (v != null && v.Exists())
        {
            Vector3 dim = v.Model.Dimensions.rearBottomLeft, dim2 = v.Model.Dimensions.frontTopRight;
            float len = Math.Max(3f, dim2.Y - dim.Y), height = Math.Max(1.2f, dim2.Z - dim.Z);
            pivot = v.Position + new Vector3(0, 0, height * 0.55f + 0.4f);
            dist = len * (1.3f + 0.35f * camDistStep) + 1.5f;
            // swing behind the car when the player isn't steering the camera
            if ((DateTime.Now - lastLookInput).TotalSeconds > 1.2 && v.Speed > 3f)
            {
                float target = v.Heading; if (Vector3.Dot(v.Velocity, v.ForwardVector) < 0) target += 180f;
                camYaw = LerpAngle(camYaw, target, Math.Min(1f, dt * 2.5f));
                camPitch = camPitch + (-10f - camPitch) * Math.Min(1f, dt * 1.5f);
            }
        }
        else
        {
            pivot = p2.Position + new Vector3(0, 0, 0.55f);
            dist = aiming ? 1.9f : 3.2f + 1.0f * camDistStep;
            side = aiming ? 0.55f : 0.25f;
        }
        camDist += (dist - camDist) * Math.Min(1f, dt * 8f);
        float viewYaw = camYaw + (v != null && v.Exists() && Held(Pad.RS) ? 180f : 0f);   // RS in a car = look behind
        camFwd = Forward(viewYaw, camPitch);
        Vector3 right = Vector3.Cross(camFwd, Vector3.WorldUp).Normalized;
        // smooth the point the camera follows (damped, frame-rate independent) so it never jitters
        float k = 1f - (float)Math.Exp(-dt * 16f);
        smoothPivot = smoothReady && smoothPivot.DistanceTo(pivot) < 8f ? Vector3.Lerp(smoothPivot, pivot, k) : pivot; smoothReady = true;
        pivot = smoothPivot;
        Vector3 want = pivot - camFwd * camDist + right * side;
        RaycastResult hit = World.Raycast(pivot, want, IntersectFlags.Map | IntersectFlags.Vehicles | IntersectFlags.Objects, (Entity)v ?? p2);
        // walls: pull in instantly, ease back out (no popping in and out)
        float allowed = hit.DidHit ? Math.Max(0.35f, hit.HitPosition.DistanceTo(pivot) - 0.25f) : camDist;
        collDist = allowed < collDist ? allowed : collDist + (allowed - collDist) * Math.Min(1f, dt * 3f);
        if (collDist > camDist) collDist = camDist;
        want = pivot + (want - pivot).Normalized * collDist;
        camPos = want;
        if (firstPerson && (v == null || !v.Exists())) { camPos = FirstPersonPos(); }
        if (firstPerson && (v == null || !v.Exists())) { camPos = FirstPersonPos(); }
        cam.Position = camPos;
        cam.Rotation = new Vector3(camPitch, 0f, viewYaw);
        cam.FieldOfView = aiming ? 42f : 58f;

        // where Player 2 is aiming: whatever is in the middle of their screen
        Vector3 far = camPos + camFwd * 200f;
        RaycastResult aim = World.Raycast(camPos + camFwd * camDist, far, IntersectFlags.Everything, (Entity)v ?? p2);
        aimPoint = aim.DidHit ? aim.HitPosition : far;
        if (aimProp != null && aimProp.Exists()) aimProp.Position = aimPoint;
    }

    // Controller aim assist like GTA's own: pulling LT snaps to the nearest target close to the
    // crosshair, and while aiming the view gently sticks to targets passing under it.
    bool wasAiming;
    void AimAssist(float dt, bool justStarted)
    {
        Vector3 fwd = Forward(camYaw, camPitch);
        Ped p1 = Game.Player.Character;
        float bestAng = justStarted ? 18f : 5f; Vector3 bestPt = Vector3.Zero; bool found = false;
        foreach (Ped q in World.GetNearbyPeds(p2.Position, 70f))
        {
            if (q == null || q == p2 || q == p1 || !q.Exists() || q.IsDead) continue;
            Vector3 chest = q.Bones[Bone.SkelSpine3].Position;
            Vector3 to = chest - camPos; float dist = to.Length(); if (dist < 1.5f) continue;
            float ang = (float)(Math.Acos(Clamp(Vector3.Dot(to / dist, fwd), -1f, 1f)) * 180.0 / Math.PI);
            if (ang >= bestAng) continue;
            if (World.Raycast(camPos, chest, IntersectFlags.Map, p2).DidHit) continue;   // hidden behind a wall
            bestAng = ang; bestPt = chest; found = true;
        }
        if (!found) return;
        Vector3 d = (bestPt - camPos).Normalized;
        float wantYaw = (float)(Math.Atan2(-d.X, d.Y) * 180.0 / Math.PI);
        float wantPitch = (float)(Math.Asin(Clamp(d.Z, -1f, 1f)) * 180.0 / Math.PI);
        float t = justStarted ? 1f : Math.Min(1f, dt * 3.5f);
        camYaw = LerpAngle(camYaw, wantYaw, t);
        camPitch += (wantPitch - camPitch) * t;
    }

    void RenderP2(bool on)
    {
        if (cam != null && cam.Exists()) Function.Call(Hash.SET_CAM_ACTIVE, cam, on);
        Function.Call(Hash.RENDER_SCRIPT_CAMS, on, false, 0, true, false, 0);
    }

    void LockMapToP2()
    {
        if (!Alive(p2)) return;
        Function.Call(Hash.LOCK_MINIMAP_POSITION, p2.Position.X, p2.Position.Y);
        Function.Call(Hash.LOCK_MINIMAP_ANGLE, (int)NormAngle(camYaw));
    }

    void UnlockMap()
    {
        Function.Call(Hash.UNLOCK_MINIMAP_POSITION);
        Function.Call(Hash.UNLOCK_MINIMAP_ANGLE);
    }

    // ------------------------------------------------------------------ on foot
    void OnFootControls(Ped p1)
    {
        if (p2.IsRagdoll || p2.IsGettingUp || p2.IsFalling || p2.IsInParachuteFreeFall) { taskKind = "busy"; return; }

        // getting into a car: leave it alone unless B cancels
        if (p2.IsGettingIntoVehicle || p2.VehicleTryingToEnter != null)
        {
            if (Pressed(Pad.B) || pad.LX * pad.LX + pad.LY * pad.LY > 0.95f && taskKind == "enter" && Game.GameTime - taskTime > 600)
            { p2.Task.ClearAll(); taskKind = ""; }
            return;
        }
        if (taskKind == "enter" && Game.GameTime - taskTime < 400) return;   // let the task start

        // Y: tap = driver, hold = passenger
        if (Pressed(Pad.Y)) { yDownAt = Game.GameTime; yHandled = false; }
        if (yDownAt >= 0 && !yHandled)
        {
            bool held = Held(Pad.Y);
            if (!held || Game.GameTime - yDownAt > 350) { yHandled = true; yDownAt = -1; if (TryEnterVehicle(p1, held)) return; }
        }

        if (Pressed(Pad.DDown)) { TeleportToP1(p1); return; }
        if (Pressed(Pad.DRight) || Pressed(Pad.RB)) CycleWeapon(+1);
        if (Pressed(Pad.DLeft)) CycleWeapon(-1);

        if (Pressed(Pad.LS))   // crouch / sneak
        {
            bool sneaking = Function.Call<bool>(Hash.GET_PED_STEALTH_MOVEMENT, p2);
            Function.Call(Hash.SET_PED_STEALTH_MOVEMENT, p2, !sneaking, "DEFAULT_ACTION");
        }
        if (Pressed(Pad.X)) { p2.Task.Climb(); taskKind = "jump"; taskTime = Game.GameTime; return; }
        if (taskKind == "jump" && (Game.GameTime - taskTime < 900 || p2.IsJumping || p2.IsClimbing)) return;

        bool armed = IsGun(p2.Weapons.Current.Hash);
        if (Pressed(Pad.B) && armed) { p2.Task.ReloadWeapon(); taskKind = "reload"; taskTime = Game.GameTime; return; }
        if (taskKind == "reload" && Game.GameTime - taskTime < 1200 && !(pad.RT > 0.4f)) return;

        Vector2 stick = new Vector2(pad.LX, pad.LY);
        float mag = Math.Min(1f, stick.Length());
        Vector3 moveDir = Vector3.Zero;
        if (mag > 0.05f)
        {
            Vector3 f = new Vector3(camFwd.X, camFwd.Y, 0).Normalized;
            Vector3 r = Vector3.Cross(f, Vector3.WorldUp).Normalized;
            moveDir = (f * stick.Y + r * stick.X).Normalized;
        }
        float speed = Held(Pad.A) ? 3f : (mag > 0.8f ? 2f : 1f);
        bool aiming = pad.LT > 0.35f, shooting = pad.RT > 0.4f;

        if (!armed && shooting && !aiming) { Melee(); return; }
        if (armed && (aiming || shooting))
        {
            if (mag > 0.05f)
                Issue(shooting ? "aimmove-shoot" : "aimmove", moveDir, () =>
                    Function.Call(Hash.TASK_GO_TO_COORD_WHILE_AIMING_AT_ENTITY, p2,
                        p2.Position.X + moveDir.X * 6f, p2.Position.Y + moveDir.Y * 6f, p2.Position.Z,
                        aimProp, speed > 1.5f ? 2f : 1f, shooting, 0.5f, 4f, false, 0, true, unchecked((int)0xC6EE6B4C)), 18f, 450);
            else if (shooting)
                Issue("shoot", Vector3.Zero, () => Function.Call(Hash.TASK_SHOOT_AT_ENTITY, p2, aimProp, -1, unchecked((int)0xC6EE6B4C)), 0, 0);
            else
                Issue("aim", Vector3.Zero, () => Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, p2, aimProp, -1, true), 0, 0);
            return;
        }
        if (mag > 0.05f)
        {
            string kind = "move" + (int)speed;
            Issue(kind, moveDir, () =>
            {
                Vector3 t = p2.Position + moveDir * 8f;
                float heading = NormAngle((float)(Math.Atan2(-moveDir.X, moveDir.Y) * 180.0 / Math.PI));
                Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, p2, t.X, t.Y, t.Z, speed, -1, heading, 0f);
            }, 14f, 500);
            return;
        }
        if (taskKind.StartsWith("move") || taskKind.StartsWith("aim") || taskKind == "shoot" || taskKind == "melee" || taskKind == "reload")
        {
            Function.Call(Hash.CLEAR_PED_TASKS, p2);
            taskKind = "";
        }
    }

    // only re-task when something meaningful changed, so movement stays smooth
    void Issue(string kind, Vector3 dir, Action start, float maxAngle, int refreshMs)
    {
        bool changed = kind != taskKind;
        if (!changed && maxAngle > 0)
        {
            float cos = Vector3.Dot(dir, taskDir);
            if (cos < Math.Cos(maxAngle * Math.PI / 180.0)) changed = true;
            else if (refreshMs > 0 && Game.GameTime - taskTime > refreshMs) changed = true;
        }
        if (!changed) return;
        start();
        taskKind = kind; taskDir = dir; taskTime = Game.GameTime;
    }

    void Melee()
    {
        if (taskKind == "melee" && Game.GameTime - taskTime < 700) return;
        Ped target = World.GetNearbyPeds(p2.Position, 3f).Where(q => q != p2).Where(q => q != Game.Player.Character && q.Exists() && !q.IsDead)
            .OrderBy(q => Vector3.Dot((q.Position - p2.Position).Normalized, new Vector3(camFwd.X, camFwd.Y, 0).Normalized) < 0.2f ? 1 : 0)
            .ThenBy(q => q.Position.DistanceTo(p2.Position)).FirstOrDefault();
        if (target == null) return;
        Function.Call(Hash.TASK_PUT_PED_DIRECTLY_INTO_MELEE, p2, target, 0f, -1f, 0f, 0);
        taskKind = "melee"; taskTime = Game.GameTime;
    }

    bool TryEnterVehicle(Ped p1, bool wantPassenger)
    {
        Vector3 facing = new Vector3(camFwd.X, camFwd.Y, 0).Normalized;
        Vehicle best = null; float bestScore = float.MaxValue;
        foreach (Vehicle v in World.GetNearbyVehicles(p2.Position, 8f))
        {
            if (!v.Exists() || v.IsDead || !v.IsDriveable) continue;
            if (v.Model.IsTrain) continue;
            Vector3 to = v.Position - p2.Position; float d = to.Length();
            float score = d - 1.5f * Vector3.Dot(to.Normalized, facing);
            if (v == p1.CurrentVehicle) score -= 2f;          // prefer Player 1's car
            if (score < bestScore) { bestScore = score; best = v; }
        }
        if (best == null) { Notify("No car close enough"); return false; }

        VehicleSeat seat;
        Ped driver = best.Driver;
        bool p1Drives = driver != null && driver == p1;
        if (!wantPassenger && !p1Drives) seat = VehicleSeat.Driver;
        else
        {
            seat = VehicleSeat.None;
            int seats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, best);
            for (int i = 0; i < seats; i++) if (best.IsSeatFree((VehicleSeat)i)) { seat = (VehicleSeat)i; break; }
            if (seat == VehicleSeat.None) { if (!p1Drives && best.IsSeatFree(VehicleSeat.Driver)) seat = VehicleSeat.Driver; else { Notify("That car is full"); return false; } }
        }
        p2.Task.EnterVehicle(best, seat, 10000, 2f, EnterVehicleFlags.ResumeIfInterupted | (p1Drives ? 0 : EnterVehicleFlags.JackAnyone));
        taskKind = "enter"; taskTime = Game.GameTime;
        Log("P2 entering " + best.DisplayName + " seat " + seat);
        return true;
    }

    void TeleportToP1(Ped p1)
    {
        if (!Alive(p1)) return;
        if (p1.IsInVehicle())
        {
            Vehicle v = p1.CurrentVehicle;
            int seats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, v);
            for (int i = 0; i < seats; i++) if (v.IsSeatFree((VehicleSeat)i)) { p2.Task.WarpIntoVehicle(v, (VehicleSeat)i); return; }
        }
        p2.Position = p1.GetOffsetPosition(new Vector3(1.2f, -0.5f, 0f));
        p2.Heading = p1.Heading;
        camYaw = p1.Heading;
    }

    // ------------------------------------------------------------------ vehicles
    void VehicleControls()
    {
        Vehicle v = p2.CurrentVehicle;
        if (v == null || !v.Exists()) return;
        if (p2.IsExitingVehicle || p2.IsJumpingOutOfVehicle) return;

        if (Pressed(Pad.Y))
        {
            if (v.Speed > 9f) p2.Task.LeaveVehicle(LeaveVehicleFlags.BailOut);
            else p2.Task.LeaveVehicle(LeaveVehicleFlags.None);
            taskKind = "exit"; taskTime = Game.GameTime; return;
        }
        if (Pressed(Pad.DDown)) { TeleportToP1(Game.Player.Character); return; }
        if (Pressed(Pad.DRight) || Pressed(Pad.RB)) CycleWeapon(+1);
        if (Pressed(Pad.DLeft)) CycleWeapon(-1);
        if (Pressed(Pad.X) && v.Model.IsCar) { Function.Call(Hash.TASK_SHUFFLE_TO_NEXT_VEHICLE_SEAT, p2, v, 0); return; }

        if (v.Handle != lastDrivenVehicle) { lastDrivenVehicle = v.Handle; directDriveOk = driveMode == "direct"; directProbeStart = -1; }
        bool isDriver = v.Driver == p2;
        if (isDriver) Drive(v); else Passenger(v);
    }

    void Drive(Vehicle v)
    {
        if (Fly(v)) return;
        float gas = pad.RT, brake = pad.LT, steer = pad.LX;
        float fwdSpeed = Vector3.Dot(v.Velocity, v.ForwardVector);
        bool hand = Held(Pad.A);

        if (driveMode == "direct" && directDriveOk)
        {
            if (taskKind != "drive") { Function.Call(Hash.CLEAR_PED_TASKS, p2); taskKind = "drive"; taskTime = Game.GameTime; }
            float throttle = gas, brakeIn = 0f;
            if (brake > 0.05f) { if (fwdSpeed > 1.0f) brakeIn = brake; else throttle = -brake; }
            v.Throttle = throttle;
            v.ThrottlePower = throttle * 100f;
            v.BrakePower = brakeIn;
            v.SteeringAngle = -steer * 38f;
            v.IsHandbrakeForcedOn = hand;
            // make sure the direct method really moves the car (checked once per car, from a standstill);
            // if full gas for 2 s leaves it parked, fall back to GTA's driving actions
            if (v.Handle != probedVehicle && gas > 0.8f && v.Speed < 1f && directProbeStart < 0) { directProbeStart = Game.GameTime; }
            if (directProbeStart >= 0 && gas < 0.5f) directProbeStart = -1;
            if (directProbeStart >= 0 && Game.GameTime - directProbeStart > 2000)
            {
                probedVehicle = v.Handle; directProbeStart = -1;
                if (v.Speed < 1.5f && !v.IsInAir && v.IsOnAllWheels) { directDriveOk = false; Log("direct driving had no effect on " + v.DisplayName + " - using task driving for it"); }
                else Log("direct driving works on " + v.DisplayName);
            }
            return;
        }

        // task driving: pick the GTA driving action closest to the stick/triggers
        int action;
        bool left = steer < -0.3f, right = steer > 0.3f;
        if (hand) action = 6;
        else if (gas > 0.1f) action = left ? 7 : right ? 8 : (gas > 0.6f ? 32 : 9);
        else if (brake > 0.1f) action = fwdSpeed > 1f ? (left ? 4 : right ? 5 : 24) : (left ? 13 : right ? 14 : 28);
        else action = left ? 10 : right ? 11 : 27;
        string kind = "temp" + action;
        if (kind != taskKind || Game.GameTime - taskTime > 300)
        {
            Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, p2, v, action, 400);
            taskKind = kind; taskTime = Game.GameTime;
        }
        Function.Call(Hash.SET_VEHICLE_STEER_BIAS, v, -steer);
    }

    void Passenger(Vehicle v)
    {
        bool shooting = pad.RT > 0.4f, aiming = pad.LT > 0.35f;
        if ((shooting || aiming) && IsGun(p2.Weapons.Current.Hash))
        {
            if (taskKind != "driveby" || Game.GameTime - taskTime > 350)
            {
                Function.Call(Hash.TASK_DRIVE_BY, p2, 0, 0, aimPoint.X, aimPoint.Y, aimPoint.Z, 200f, 100, false,
                              shooting ? unchecked((int)0xC6EE6B4C) : unchecked((int)0x7A845691));   // full auto / single shot
                taskKind = "driveby"; taskTime = Game.GameTime;
            }
        }
        else if (taskKind == "driveby")
        {
            Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, p2);
            Function.Call(Hash.CLEAR_PED_TASKS, p2);
            taskKind = "";
        }
    }

    // Player 1 walking up to Player 2's car: take a passenger seat instead of dragging Player 2 out
    void RedirectPlayer1IntoMyCar(Ped p1)
    {
        if (!Alive(p1) || !p2.IsInVehicle()) return;
        Vehicle mine = p2.CurrentVehicle;
        if (mine.Driver != p2) return;
        Vehicle trying = p1.VehicleTryingToEnter;
        if (trying == null || trying != mine) return;
        if (Function.Call<int>(Hash.GET_SEAT_PED_IS_TRYING_TO_ENTER, p1) != -1) return;
        int seats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, mine);
        for (int i = 0; i < seats; i++)
            if (mine.IsSeatFree((VehicleSeat)i))
            {
                p1.Task.EnterVehicle(mine, (VehicleSeat)i, 8000, 2f, EnterVehicleFlags.ResumeIfInterupted);
                Log("P1 redirected to passenger seat " + i);
                return;
            }
    }

    // ------------------------------------------------------------------ weapons
    static bool IsGun(WeaponHash h)
    {
        if (h == WeaponHash.Unarmed) return false;
        int group = Function.Call<int>(Hash.GET_WEAPONTYPE_GROUP, (uint)h);
        return group != unchecked((int)0xD49321D4) /* melee */ && group != 0 && group != unchecked((int)0xA00FC1E4) /* unarmed */;
    }

    void ShareWeapons(bool initial)
    {
        lastShareTime = Game.GameTime;
        Ped p1 = Game.Player.Character;
        if (!Alive(p1) || !Alive(p2)) return;
        foreach (WeaponHash h in AllWeapons)
        {
            if (h == WeaponHash.Unarmed) continue;
            if (!Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, p1, (uint)h, false)) continue;
            if (Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, p2, (uint)h, false)) continue;
            p2.Weapons.Give(h, 9999, false, true);
            if (!initial) Log("shared weapon " + h);
        }
    }

    void CycleWeapon(int dir)
    {
        List<WeaponHash> have = new List<WeaponHash> { WeaponHash.Unarmed };
        foreach (WeaponHash h in AllWeapons)
            if (h != WeaponHash.Unarmed && Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, p2, (uint)h, false)) have.Add(h);
        int i = have.IndexOf(p2.Weapons.Current.Hash);
        if (i < 0) i = 0;
        i = (i + dir + have.Count) % have.Count;
        Function.Call(Hash.SET_CURRENT_PED_WEAPON, p2, (uint)have[i], true);
        weaponToastUntil = Game.GameTime + 1500;
    }
    int weaponToastUntil;

    // ------------------------------------------------------------------ Player 2 HUD (drawn only into Player 2's frames)
    void DrawP2Hud()
    {
        if (!Alive(p2)) return;
        bool aiming = pad.LT > 0.35f || pad.RT > 0.4f;
        if (aiming && IsGun(p2.Weapons.Current.Hash))
        {
            Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 0.0035f, 0.0062f, 255, 255, 255, 230, false);
        }
        DrawNativeBars();
    }

    // drawn into Player 1's frames only
    void DrawP1Hud() { }

    void DrawDistanceWarning(string other)
    {
        Ped p1 = Game.Player.Character;
        if (!Alive(p1) || !Alive(p2)) return;
        float d = p1.Position.DistanceTo(p2.Position);
        if (d < 250f) return;
        DrawText(other + " is " + (int)d + " m away - the world around the far player gets thinner past 250 m",
                 0.5f, 0.92f, 0.38f, Alignment.Center);
    }

    // Player 2's health and armour drawn into GTA's own bar under the minimap (Player 2's frames only),
    // at the exact pixels and colours the game uses (measured at 1920x1080, default safe zone).
    void DrawNativeBars()
    {
        float hp = Clamp((p2.Health - 100f) / Math.Max(1f, p2.MaxHealth - 100f), 0f, 1f);
        float ar = Clamp(p2.Armor / 100f, 0f, 1f);
        const float Y = 1053f / 1080f, H = 9f / 1080f;
        Bar(28f, 164f, Y, H, hp, 32, 58, 31, 81, 146, 77);      // health: dark green track, green fill
        Bar(166f, 231f, Y, H, ar, 40, 78, 97, 76, 150, 196);    // armour: dark blue track, blue fill
    }

    static void Bar(float x0px, float x1px, float y, float h, float fill, int tr, int tg, int tb, int fr, int fg, int fb)
    {
        float x0 = x0px / 1920f, w = (x1px - x0px) / 1920f;
        Function.Call(Hash.DRAW_RECT, x0 + w / 2, y, w, h, tr, tg, tb, 255, false);
        if (fill > 0.001f) Function.Call(Hash.DRAW_RECT, x0 + w * fill / 2, y, w * fill, h, fr, fg, fb, 255, false);
    }

    // GTA's own "wasted" banner (the game's big-message movie, the game's own text label and sound)
    int wastedMovie;
    void StartWasted()
    {
        wastedMovie = Function.Call<int>(Hash.REQUEST_SCALEFORM_MOVIE, "MP_BIG_MESSAGE_FREEMODE");
        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "Bed", "WastedSounds", true);
        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "ScreenFlash", "WastedSounds", true);
        wastedShown = false;
    }
    bool wastedShown;
    void DrawWastedNative()
    {
        if (wastedMovie == 0 || !Function.Call<bool>(Hash.HAS_SCALEFORM_MOVIE_LOADED, wastedMovie)) return;
        if (!wastedShown)
        {
            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, wastedMovie, "SHOW_SHARD_WASTED_MP_MESSAGE");
            Function.Call(Hash.BEGIN_TEXT_COMMAND_SCALEFORM_STRING, "RESPAWN_W");   // the game's own "wasted" text
            Function.Call(Hash.END_TEXT_COMMAND_SCALEFORM_STRING);
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_TEXTURE_NAME_STRING, "");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, 5);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);
            wastedShown = true;
        }
        Function.Call(Hash.DRAW_SCALEFORM_MOVIE_FULLSCREEN, wastedMovie, 255, 255, 255, 255, 0);
    }

    // Tiny colour tag in the top-left corner: TwoScreens.asi reads it on the graphics card to know whose
    // picture this frame is (magenta = Player 2, green = Player 1) and paints over it.
    void DrawFrameTag(int whose)
    {
        float w = 6f / GTA.UI.Screen.Resolution.Width, h = 6f / GTA.UI.Screen.Resolution.Height;
        if (whose == 2) Function.Call(Hash.DRAW_RECT, w / 2, h / 2, w, h, 255, 0, 255, 255, false);
        else Function.Call(Hash.DRAW_RECT, w / 2, h / 2, w, h, 0, 255, 0, 255, false);
    }

    // when not split, a small Player 2 health line on the shared screen
    void DrawP2HudSmall()
    {
        if (!Alive(p2)) return;

    }

    void DrawBars(float x, float y)
    {
        float hp = Clamp((p2.Health - 100f) / Math.Max(1f, p2.MaxHealth - 100f), 0f, 1f);
        float ar = Clamp(p2.Armor / 100f, 0f, 1f);
        const float W = 0.07f, H = 0.008f;
        Function.Call(Hash.DRAW_RECT, x + W / 2, y + 0.032f, W, H, 0, 0, 0, 150, false);
        Function.Call(Hash.DRAW_RECT, x + W * hp / 2, y + 0.032f, W * hp, H, 110, 200, 110, 230, false);
        Function.Call(Hash.DRAW_RECT, x + W / 2, y + 0.044f, W, H, 0, 0, 0, 150, false);
        Function.Call(Hash.DRAW_RECT, x + W * ar / 2, y + 0.044f, W * ar, H, 90, 160, 230, 230, false);
    }

    void DrawText(string t, float x, float y, float scale, Alignment align)
    {
        var te = new TextElement(t, new PointF(x * GTA.UI.Screen.Width, y * GTA.UI.Screen.Height), scale, Color.White, GTA.UI.Font.ChaletLondon, align, true, true);
        te.Draw();
    }

    static string WeaponLabel(WeaponHash h)
    {
        if (h == WeaponHash.Unarmed) return "Fists";
        string s = h.ToString();
        return System.Text.RegularExpressions.Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
    }

    // ------------------------------------------------------------------ test commands (CoopPlus\cmd.txt)
    Queue<string> pendingCmds = new Queue<string>(); int nextCmdAt;

    void RunCommands()
    {
        if (File.Exists(cmdPath))
        {
            try { foreach (string l in File.ReadAllLines(cmdPath)) pendingCmds.Enqueue(l); File.Delete(cmdPath); } catch { }
        }
        while (pendingCmds.Count > 0 && Game.GameTime >= nextCmdAt)
        {
            string line = pendingCmds.Dequeue().Trim(); if (line.Length == 0) continue;
            string[] a = line.Split(' ');
            Log("cmd: " + line);
            if (a[0] == "wait") { nextCmdAt = Game.GameTime + int.Parse(a[1]); continue; }
            try
            {
                switch (a[0])
                {
                    case "spawn": if (!Alive(p2)) Spawn(a.Length > 1 ? int.Parse(a[1]) : padSetting); break;
                    case "despawn": if (p2 != null) Despawn("removed"); break;
                    case "split": split = a.Length < 2 || a[1] == "on"; break;
                    case "pad":   // pad <ms> lx ly rx ry lt rt buttons
                        virtualPad = new Pad
                        {
                            LX = F(a[2]), LY = F(a[3]), RX = F(a[4]), RY = F(a[5]), LT = F(a[6]), RT = F(a[7]),
                            Buttons = Convert.ToInt32(a[8], 16)
                        };
                        virtualUntil = Game.GameTime + int.Parse(a[1]); break;
                    case "car":
                        Vehicle c = World.CreateVehicle(new Model(a.Length > 1 ? a[1] : "sultan"), p2.GetOffsetPosition(new Vector3(0, 5f, 0)), p2.Heading + 90f);
                        if (c != null) c.MarkAsNoLongerNeeded(); break;
                    case "tp": TeleportToP1(Game.Player.Character); break;
                    case "drivemode": driveMode = a[1]; directDriveOk = true; break;
                    case "lag": hudLag = int.Parse(a[1]); mapLag = int.Parse(a[2]); break;
                    case "state": LogState(); break;
                    case "mark": Log("MARK " + line.Substring(4).Trim()); break;
                    case "p1car":   // put a car in front of Player 1
                        Vehicle c1 = World.CreateVehicle(new Model(a.Length > 1 ? a[1] : "sultan"), Game.Player.Character.GetOffsetPosition(new Vector3(0, 6f, 0)), Game.Player.Character.Heading + 90f);
                        if (c1 != null) c1.MarkAsNoLongerNeeded(); break;
                    case "p1enter": // Player 1 gets into the nearest car (simulates pressing F)
                        Vehicle near = World.GetNearbyVehicles(Game.Player.Character.Position, 10f).OrderBy(x => x.Position.DistanceTo(Game.Player.Character.Position)).FirstOrDefault();
                        if (near != null) Game.Player.Character.Task.EnterVehicle(near, a.Length > 1 ? (VehicleSeat)int.Parse(a[1]) : VehicleSeat.Driver, 8000, 2f, EnterVehicleFlags.None); break;
                    case "p1exit": Game.Player.Character.Task.LeaveVehicle(); break;
                    case "p1weapon": Game.Player.Character.Weapons.Give((WeaponHash)Enum.Parse(typeof(WeaponHash), a[1]), 500, true, true); break;
                    case "p2face": // turn Player 2's camera to look at Player 1 (or at a heading)
                        camYaw = a.Length > 1 ? F(a[1]) : NormAngle((float)(Math.Atan2(-(Game.Player.Character.Position - p2.Position).X, (Game.Player.Character.Position - p2.Position).Y) * 180.0 / Math.PI)); camPitch = -5f; break;
                    case "pickup":   // drop a pickup next to Player 2: pickup health|armour|pistol
                        string kind = a.Length > 1 ? a[1] : "health";
                        PickupType pt = kind == "armour" ? PickupType.Armour : kind == "pistol" ? PickupType.WeaponPistol : PickupType.Health;
                        World.CreatePickup(pt, p2.GetOffsetPosition(new Vector3(0, 1.0f, 0)), new Model(kind == "armour" ? "prop_armour_pickup" : kind == "pistol" ? "w_pi_pistol" : "prop_ld_health_pack"), 100);
                        break;
                    case "held": Log("held entities: " + held.Count); break;
                    case "layout": layout = int.Parse(a[1]); Log("layout " + layout); break;
                    case "revive": TryRevive(); break;
                    case "p1wanted": Game.Player.WantedLevel = int.Parse(a[1]); break;
                    case "p1tp": { Vector3 to = new Vector3(F(a[1]), F(a[2]), F(a[3])); Function.Call(Hash.REQUEST_COLLISION_AT_COORD, to.X, to.Y, to.Z); Game.Player.Character.Position = to; nextCmdAt = Game.GameTime + 2500; catchUpAt = Game.GameTime + 1500; } break;
                    case "carhere": { Vector3 st = World.GetNextPositionOnStreet(p2.Position + new Vector3(0, 0, 0)); Vehicle cv = World.CreateVehicle(new Model(a.Length > 1 ? a[1] : "sultan"), st, 0f); if (cv != null) { cv.PlaceOnGround(); p2.Position = cv.GetOffsetPosition(new Vector3(-2.2f, 0, 0)); camYaw = cv.Heading; cv.MarkAsNoLongerNeeded(); Log("car placed on the street"); } } break;
                    case "fresh": StopActivities(); p2Wanted = 0; if (p2 != null && p2.Exists()) Despawn("reset for a test"); Spawn(a.Length > 1 ? int.Parse(a[1]) : 0); split = true; break;
                    case "wanted": AddP2Wanted(int.Parse(a[1]), "test"); break;
                    case "heli": Vehicle hv = World.CreateVehicle(new Model("frogger"), p2.GetOffsetPosition(new Vector3(0, 12f, 0)), p2.Heading); if (hv != null) { p2.Task.WarpIntoVehicle(hv, VehicleSeat.Driver); hv.MarkAsNoLongerNeeded(); } break;
                    case "identify": identifyUntil = Game.GameTime + 4000; break;
                    case "share": frameShare = int.Parse(a[1]); Log("frame share " + frameShare); break;
                    case "money": LoadMoney(); p2Money = int.Parse(a[1]); Save("Player2Money", "Money", p2Money); Log("money " + p2Money); break;
                    case "activity": StopActivities(); if (a[1] == "survival") StartSurvival(); else if (a[1] == "race") StartRace(); else if (a[1] == "dm") { deathmatch = true; dm1 = dm2 = 0; Log("deathmatch started"); } break;
                    case "weapon": Log("P2 weapon " + p2.Weapons.Current.Hash); break;
                    case "hurt": p2.Health = 150; p2.Armor = 0; break;
                    case "kill": p2.Kill(); break;
                    case "ped": Ped tp = World.CreatePed(new Model("a_m_y_hipster_01"), p2.GetOffsetPosition(new Vector3(F(a[1]), F(a[2]), 0))); if (tp != null) tp.MarkAsNoLongerNeeded(); break;
                    case "hp": Log("HP p1 " + Game.Player.Character.Health + " p2 " + (p2 != null ? p2.Health : -1)); break;
                    case "camyaw": camYaw = F(a[1]); break;
                }
            }
            catch (Exception e) { Log("cmd failed: " + e.Message); }
        }
    }

    static float F(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

    void LogState()
    {
        if (!Alive(p2)) { Log("state: no player 2"); return; }
        Vehicle v = p2.CurrentVehicle;
        if (cam != null && cam.Exists())
        {
            Vector3 real = Function.Call<Vector3>(Hash.GET_CAM_COORD, cam);
            Vector3 gameCam = Function.Call<Vector3>(Hash.GET_FINAL_RENDERED_CAM_COORD);
            Log(string.Format("cam: wanted {0:F1},{1:F1},{2:F1} actual {3:F1},{4:F1},{5:F1} active {6} rendering {7:F1},{8:F1},{9:F1} handle {10} scriptCamRendering {11}",
                camPos.X, camPos.Y, camPos.Z, real.X, real.Y, real.Z, Function.Call<bool>(Hash.IS_CAM_ACTIVE, cam),
                gameCam.X, gameCam.Y, gameCam.Z, cam.Handle, Function.Call<bool>(Hash.IS_CAM_RENDERING, cam)));
        }
        XInputGame.State gs; uint seen = XInputGame.Get((uint)Math.Max(0, padSlot), out gs);
        Ped p1 = Game.Player.Character;
        Log(string.Format("state: pos {0:F1},{1:F1},{2:F1} task {3} weapon {4} hp {5} veh {6} spd {7:F1} seat {8} split {9} view {10} pad {11} | visible {12} alpha {13} dist {14:F0} | GTA sees P2 pad: {15} | P1 weapon {16} veh {17}",
            p2.Position.X, p2.Position.Y, p2.Position.Z, taskKind, p2.Weapons.Current.Hash, p2.Health,
            v != null ? v.DisplayName : "-", v != null ? v.Speed : p2.Speed, v != null ? p2.SeatIndex.ToString() : "-",
            split, view, padSlot, p2.IsVisible, Function.Call<int>(Hash.GET_ENTITY_ALPHA, p2), p2.Position.DistanceTo(p1.Position),
            seen == 0 ? "YES (buttons " + gs.Buttons.ToString("X") + ")" : "no", p1.Weapons.Current.Hash,
            p1.CurrentVehicle != null ? p1.CurrentVehicle.DisplayName : "-"));
    }

    // ------------------------------------------------------------------ helpers
    static Vector3 Forward(float yaw, float pitch)
    {
        double y = yaw * Math.PI / 180.0, p = pitch * Math.PI / 180.0;
        return new Vector3((float)(-Math.Sin(y) * Math.Cos(p)), (float)(Math.Cos(y) * Math.Cos(p)), (float)Math.Sin(p));
    }
    static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
    static float NormAngle(float a) { a %= 360f; return a < 0 ? a + 360f : a; }
    static float LerpAngle(float a, float b, float t)
    {
        float d = ((b - a) % 360f + 540f) % 360f - 180f;
        return a + d * t;
    }
}

// ---------------------------------------------------------------------- in-game menu (GTA's own menu look)
public class MenuItem
{
    public string Label, Help;
    public Func<string> Value;        // shown on the right ("On", "Michael", ...); null = plain action
    public Action<int> Change;        // left/right (-1/+1); for actions, Enter calls Change(0)
}

public class CoopMenu
{
    public bool Open; public int Owner;             // 1 = Player 1 (keyboard, main screen), 2 = Player 2 (pad, TV)
    public string Title = "Co-op", Sub = "SETTINGS";
    public List<MenuItem> Items = new List<MenuItem>();
    int sel, top; const int Visible = 10;
    public int Selected { get { return sel; } }
    public void ResetSelection() { sel = 0; top = 0; }
    bool texReady;

    public void Show(int owner) { Open = true; Owner = owner; sel = 0; top = 0; Sound("SELECT"); }
    public void Hide() { Open = false; Sound("BACK"); }

    static void Sound(string s) { Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, s, "HUD_FRONTEND_DEFAULT_SOUNDSET", true); }

    public void Move(int d)
    {
        if (Items.Count == 0) return;
        sel = (sel + d + Items.Count) % Items.Count;
        if (sel < top) top = sel; if (sel >= top + Visible) top = sel - Visible + 1;
        Sound("NAV_UP_DOWN");
    }
    public void Side(int d) { var it = Items[sel]; if (it.Value != null && it.Change != null) { it.Change(d); Sound("NAV_LEFT_RIGHT"); } }
    public void Accept() { var it = Items[sel]; if (it.Change != null) { it.Change(it.Value != null ? 1 : 0); Sound("SELECT"); } }

    public bool Key(Keys k)
    {
        switch (k)
        {
            case Keys.Up: Move(-1); return true;
            case Keys.Down: Move(1); return true;
            case Keys.Left: Side(-1); return true;
            case Keys.Right: Side(1); return true;
            case Keys.Enter: case Keys.Space: Accept(); return true;
            case Keys.Back: case Keys.Escape: Hide(); return true;
        }
        return false;
    }

    public void PadInput(Pad now, Pad prev)
    {
        Func<int, bool> pressed = b => (now.Buttons & b) != 0 && (prev.Buttons & b) == 0;
        bool stickUp = now.LY > 0.6f && prev.LY <= 0.6f, stickDown = now.LY < -0.6f && prev.LY >= -0.6f;
        bool stickLeft = now.LX < -0.6f && prev.LX >= -0.6f, stickRight = now.LX > 0.6f && prev.LX <= 0.6f;
        if (pressed(Pad.DUp) || stickUp) Move(-1);
        if (pressed(Pad.DDown) || stickDown) Move(1);
        if (pressed(Pad.DLeft) || stickLeft) Side(-1);
        if (pressed(Pad.DRight) || stickRight) Side(1);
        if (pressed(Pad.A)) Accept();
        if (pressed(Pad.B) || pressed(Pad.Back)) Hide();
    }

    // Drawn the way GTA's own interaction menus are: commonmenu header banner, gradient list, white
    // highlight bar, ChaletLondon text, HouseScript title.
    public void Draw()
    {
        if (!texReady)
        {
            Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, "commonmenu", true);
            texReady = Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, "commonmenu");
        }
        const float X = 0.0155f, W = 0.225f, HeadH = 0.0991f, SubH = 0.0343f, RowH = 0.0352f;
        float y = 0.045f;
        float cx = X + W / 2;
        if (texReady) Sprite("interaction_bgd", cx, y + HeadH / 2, W, HeadH, 255, 255, 255, 255);
        else Rect(cx, y + HeadH / 2, W, HeadH, 25, 92, 160, 255);
        Text(Title, cx, y + 0.012f, 1.05f, GTA.UI.Font.HouseScript, Alignment.Center, Color.White);
        y += HeadH;
        Rect(cx, y + SubH / 2, W, SubH, 0, 0, 0, 255);
        Text(Sub, X + 0.005f, y + 0.0045f, 0.35f, GTA.UI.Font.ChaletLondon, Alignment.Left, Color.FromArgb(255, 93, 182, 229));
        Text((sel + 1) + " / " + Items.Count, X + W - 0.005f, y + 0.0045f, 0.35f, GTA.UI.Font.ChaletLondon, Alignment.Right, Color.FromArgb(255, 93, 182, 229));
        y += SubH;
        int n = Math.Min(Visible, Items.Count - top);
        if (texReady) Sprite("gradient_bgd", cx, y + RowH * n / 2, W, RowH * n, 255, 255, 255, 255);
        else Rect(cx, y + RowH * n / 2, W, RowH * n, 0, 0, 0, 160);
        for (int i = 0; i < n; i++)
        {
            int idx = top + i; var it = Items[idx]; bool on = idx == sel;
            float ry = y + RowH * i;
            if (on) { if (texReady) Sprite("gradient_nav", cx, ry + RowH / 2, W, RowH, 255, 255, 255, 255); else Rect(cx, ry + RowH / 2, W, RowH, 245, 245, 245, 255); }
            Color c = on ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 245, 245, 245);
            Text(it.Label, X + 0.0055f, ry + 0.0045f, 0.35f, GTA.UI.Font.ChaletLondon, Alignment.Left, c);
            if (it.Value != null) Text((on ? "< " : "") + it.Value() + (on ? " >" : ""), X + W - 0.0055f, ry + 0.0045f, 0.35f, GTA.UI.Font.ChaletLondon, Alignment.Right, c);
        }
        y += RowH * n;
        string help = Items.Count > 0 ? Items[sel].Help : null;
        if (!string.IsNullOrEmpty(help))
        {
            Rect(cx, y + 0.003f, W, 0.003f, 0, 0, 0, 255);
            Rect(cx, y + 0.0055f + RowH / 2, W, RowH, 0, 0, 0, 160);
            Text(help, X + 0.0055f, y + 0.0095f, 0.33f, GTA.UI.Font.ChaletLondon, Alignment.Left, Color.White);
        }
    }

    static void Sprite(string name, float x, float y, float w, float h, int r, int g, int b, int a)
    { Function.Call(Hash.DRAW_SPRITE, "commonmenu", name, x, y, w, h, 0f, r, g, b, a, false, 0); }
    static void Rect(float x, float y, float w, float h, int r, int g, int b, int a)
    { Function.Call(Hash.DRAW_RECT, x, y, w, h, r, g, b, a, false); }
    static void Text(string t, float x, float y, float scale, GTA.UI.Font font, Alignment al, Color c)
    {
        var te = new TextElement(t, new PointF(x * GTA.UI.Screen.Width, y * GTA.UI.Screen.Height), scale, c, font, al, false, false);
        if (al == Alignment.Right) te.WrapWidth = x * GTA.UI.Screen.Width;
        te.Draw();
    }
}

// ---------------------------------------------------------------------- controller
public class Pad
{
    public const int DUp = 0x0001, DDown = 0x0002, DLeft = 0x0004, DRight = 0x0008, Start = 0x0010, Back = 0x0020,
                     LS = 0x0040, RS = 0x0080, LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    public float LX, LY, RX, RY, LT, RT;
    public int Buttons;
}

// PlayStation (DualShock 4 / DualSense) and Switch Pro controllers, read directly through Windows'
// joystick API, so no DS4Windows is needed. They use controller slots 10+ (10 = Windows joystick 0).
// GTA itself ignores these controllers, so nothing needs hiding from the game.
public class DirectPads
{
    [StructLayout(LayoutKind.Sequential)] struct JI { public int size, flags, x, y, z, r, u, v, buttons, num, pov, res1, res2; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct JC
    {
        public ushort mid, pid; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
        public uint xmin, xmax, ymin, ymax, zmin, zmax, nbuttons, pmin, pmax, rmin, rmax, umin, umax, vmin, vmax, caps, maxaxes, numaxes, maxbuttons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string reg; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string oem;
    }
    [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref JI j);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int joyGetDevCapsW(IntPtr id, ref JC c, int size);

    public const int SlotBase = 10;
    enum Kind { None, PlayStation, Nintendo }
    readonly Kind[] kinds = new Kind[16]; int lastScan = -100000;
    readonly int[] uRest = new int[16], vRest = new int[16]; readonly bool[] calibrated = new bool[16];

    void Scan()
    {
        if (Environment.TickCount - lastScan < 3000) return;
        lastScan = Environment.TickCount;
        for (int i = 0; i < 16; i++)
        {
            JC c = new JC(); kinds[i] = Kind.None;
            if (joyGetDevCapsW((IntPtr)i, ref c, Marshal.SizeOf(typeof(JC))) != 0) continue;
            JI j = new JI { size = Marshal.SizeOf(typeof(JI)), flags = 0xFF };
            if (joyGetPosEx(i, ref j) != 0) continue;            // listed but not plugged in
            if (c.mid == 0x054C) kinds[i] = Kind.PlayStation;       // Sony
            else if (c.mid == 0x057E) kinds[i] = Kind.Nintendo;     // Nintendo (Switch Pro)
        }
    }

    public List<int> ConnectedSlots() { Scan(); var l = new List<int>(); for (int i = 0; i < 16; i++) if (kinds[i] != Kind.None) l.Add(SlotBase + i); return l; }
    public string Describe(int slot) { int i = slot - SlotBase; return i >= 0 && i < 16 && kinds[i] == Kind.PlayStation ? "PlayStation" : "Switch"; }

    public Pad Read(int slot, float dead)
    {
        Pad p = new Pad(); int i = slot - SlotBase;
        if (i < 0 || i >= 16) return p;
        Scan(); if (kinds[i] == Kind.None) return p;
        JI j = new JI { size = Marshal.SizeOf(typeof(JI)), flags = 0xFF };
        if (joyGetPosEx(i, ref j) != 0) return p;
        int b = j.buttons; Func<int, bool> btn = n => (b & (1 << n)) != 0;
        if (kinds[i] == Kind.PlayStation)
        {   // Square 0, Cross 1, Circle 2, Triangle 3, L1 4, R1 5, L2 6, R2 7, Share 8, Options 9, L3 10, R3 11, PS 12, Pad 13
            if (btn(1)) p.Buttons |= Pad.A; if (btn(2)) p.Buttons |= Pad.B; if (btn(0)) p.Buttons |= Pad.X; if (btn(3)) p.Buttons |= Pad.Y;
            if (btn(8) || btn(13)) p.Buttons |= Pad.Back; if (btn(9)) p.Buttons |= Pad.Start;
        }
        else
        {   // Switch Pro (positions, like Xbox): B 0 south, A 1 east, Y 2 west, X 3 north, L 4, R 5, ZL 6, ZR 7, - 8, + 9, LS 10, RS 11
            if (btn(0)) p.Buttons |= Pad.A; if (btn(1)) p.Buttons |= Pad.B; if (btn(2)) p.Buttons |= Pad.X; if (btn(3)) p.Buttons |= Pad.Y;
            if (btn(8)) p.Buttons |= Pad.Back; if (btn(9)) p.Buttons |= Pad.Start;
        }
        if (btn(4)) p.Buttons |= Pad.LB; if (btn(5)) p.Buttons |= Pad.RB; if (btn(10)) p.Buttons |= Pad.LS; if (btn(11)) p.Buttons |= Pad.RS;
        if (j.pov != 0xFFFF && j.pov >= 0)
        {
            int a = j.pov / 100;
            if (a >= 315 || a <= 45) p.Buttons |= Pad.DUp; if (a >= 45 && a <= 135) p.Buttons |= Pad.DRight;
            if (a >= 135 && a <= 225) p.Buttons |= Pad.DDown; if (a >= 225 && a <= 315) p.Buttons |= Pad.DLeft;
        }
        Stick(j.x, 65535 - j.y, dead, out p.LX, out p.LY);
        Stick(j.z, 65535 - j.r, dead * 0.8f, out p.RX, out p.RY);
        // triggers: learn each axis' resting value while the trigger buttons are up; the trigger buttons
        // also count as a full press in case the axis never moves (driver differences)
        if (!btn(6) && !btn(7) && !calibrated[i]) { uRest[i] = j.u; vRest[i] = j.v; calibrated[i] = true; }
        p.LT = Math.Max(TriggerAxis(j.u, uRest[i]), btn(6) ? 1f : 0f);
        p.RT = Math.Max(TriggerAxis(j.v, vRest[i]), btn(7) ? 1f : 0f);
        if (p.LT < 0.06f) p.LT = 0; if (p.RT < 0.06f) p.RT = 0;
        return p;
    }

    static float TriggerAxis(int v, int rest)
    {
        float span = rest < 16384 ? 65535f - rest : (rest > 49151 ? rest : 32767f);
        return Math.Min(1f, Math.Abs(v - rest) / Math.Max(1f, span));
    }

    static void Stick(int x, int y, float dead, out float ox, out float oy)
    {
        float fx = (x - 32767.5f) / 32767.5f, fy = (y - 32767.5f) / 32767.5f;
        float mag = (float)Math.Sqrt(fx * fx + fy * fy);
        if (mag < dead) { ox = oy = 0; return; }
        float scaled = Math.Min(1f, (mag - dead) / (1f - dead)); scaled = scaled * scaled * 0.35f + scaled * 0.65f;
        ox = fx / mag * scaled; oy = fy / mag * scaled;
    }
}

// What GTA itself sees (goes through the same XInput the game imports, so XInputBlock applies).
public static class XInputGame
{
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [DllImport("XINPUT1_4.dll", EntryPoint = "XInputGetState")] static extern uint XInputGetState(uint i, out State s);
    public static uint Get(uint i, out State s) { try { return XInputGetState(i, out s); } catch { s = new State(); return 1167; } }
}

// Reads controllers through a private copy of xinput1_4.dll, so XInputBlock (which hides Player 2's
// controller from the game itself) doesn't hide it from us too.
public class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint GetStateFn(uint index, out State s);
    [StructLayout(LayoutKind.Sequential)] struct Vib { public ushort L, R; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint SetStateFn(uint index, ref Vib v);
    SetStateFn setState; readonly int[] vibStop = new int[4];
    public void Vibrate(int slot, float strength, int ms)
    {
        if (setState == null || slot < 0 || slot > 3) return;
        Vib v = new Vib { L = (ushort)(Math.Min(1f, strength) * 65535), R = (ushort)(Math.Min(1f, strength) * 40000) };
        setState((uint)slot, ref v); vibStop[slot] = Environment.TickCount + ms;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, IntPtr ordinal);

    GetStateFn getState;
    uint[] lastMiss = new uint[4];

    public XInput(string privateCopy, Action<string> log)
    {
        IntPtr m = File.Exists(privateCopy) ? LoadLibraryW(privateCopy) : IntPtr.Zero;
        if (m == IntPtr.Zero) { log("private xinput copy missing (" + privateCopy + "), using xinput1_3"); m = LoadLibraryW("xinput1_3.dll"); }
        if (m == IntPtr.Zero) { log("ERROR no xinput available"); return; }
        IntPtr f = GetProcAddress(m, (IntPtr)100);              // XInputGetStateEx: includes the guide button
        if (f == IntPtr.Zero) f = GetProcAddress(m, "XInputGetState");
        getState = (GetStateFn)Marshal.GetDelegateForFunctionPointer(f, typeof(GetStateFn));
        IntPtr sf = GetProcAddress(m, "XInputSetState");
        if (sf != IntPtr.Zero) setState = (SetStateFn)Marshal.GetDelegateForFunctionPointer(sf, typeof(SetStateFn));
        log("controllers read via " + privateCopy);
    }

    public bool Connected(int i)
    {
        State s; return getState != null && getState((uint)i, out s) == 0;
    }

    public Pad Read(int i, float dead)
    {
        Pad p = new Pad();
        State s;
        if (getState == null || i < 0 || i > 3 || getState((uint)i, out s) != 0) return p;
        if (vibStop[i] != 0 && Environment.TickCount > vibStop[i] && setState != null) { Vib z = new Vib(); setState((uint)i, ref z); vibStop[i] = 0; }
        p.Buttons = s.Buttons;
        Stick(s.LX, s.LY, dead, out p.LX, out p.LY);
        Stick(s.RX, s.RY, dead * 0.8f, out p.RX, out p.RY);
        p.LT = s.LT / 255f; p.RT = s.RT / 255f;
        if (p.LT < 0.06f) p.LT = 0; if (p.RT < 0.06f) p.RT = 0;
        return p;
    }

    static void Stick(short x, short y, float dead, out float ox, out float oy)
    {
        float fx = Math.Max(-1f, x / 32767f), fy = Math.Max(-1f, y / 32767f);
        float mag = (float)Math.Sqrt(fx * fx + fy * fy);
        if (mag < dead) { ox = oy = 0; return; }
        float scaled = Math.Min(1f, (mag - dead) / (1f - dead));
        scaled = scaled * scaled * 0.35f + scaled * 0.65f;     // a little finer control near the centre
        ox = fx / mag * scaled; oy = fy / mag * scaled;
    }
}
