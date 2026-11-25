// Features.cs - weapon wheel, Player 2's money and shop, revive, co-op activities, rumble.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

public partial class CoopCore
{
    // ================================================================== weapon wheel (hold LB)
    CoopMenu wheel = new CoopMenu();
    int lbDownAt = -1; bool wheelOpen;
    List<WeaponHash> wheelWeapons = new List<WeaponHash>();

    // returns true while the wheel owns Player 2's controller
    bool WeaponWheelInput(Pad raw, Pad rawPrev)
    {
        if (!Alive(p2)) { wheelOpen = false; return false; }
        bool lb = (raw.Buttons & Pad.LB) != 0, lbWas = (rawPrev.Buttons & Pad.LB) != 0;
        if (lb && !lbWas) lbDownAt = Game.GameTime;
        if (!wheelOpen && lb && lbDownAt >= 0 && Game.GameTime - lbDownAt > 220)
        {
            wheelWeapons.Clear(); wheel.Items.Clear();
            wheelWeapons.Add(WeaponHash.Unarmed);
            foreach (WeaponHash h in AllWeapons)
                if (h != WeaponHash.Unarmed && Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, p2, (uint)h, false)) wheelWeapons.Add(h);
            foreach (WeaponHash h in wheelWeapons) { WeaponHash hh = h; wheel.Items.Add(new MenuItem { Label = GameWeaponName(hh), Change = d => { } }); }
            wheel.Title = "Weapons"; wheel.Sub = "PLAYER 2";
            wheel.Show(2); wheelOpen = true;
            int cur = wheelWeapons.IndexOf(p2.Weapons.Current.Hash); for (int i = 0; i < cur; i++) wheel.Move(1);
        }
        if (wheelOpen)
        {
            bool up = (raw.Buttons & Pad.DUp) != 0 && (rawPrev.Buttons & Pad.DUp) == 0 || raw.RY > 0.6f && rawPrev.RY <= 0.6f || raw.LY > 0.6f && rawPrev.LY <= 0.6f;
            bool down = (raw.Buttons & Pad.DDown) != 0 && (rawPrev.Buttons & Pad.DDown) == 0 || raw.RY < -0.6f && rawPrev.RY >= -0.6f || raw.LY < -0.6f && rawPrev.LY >= -0.6f;
            if (up) wheel.Move(-1); if (down) wheel.Move(1);
            if (!lb)
            {   // let go of LB: equip the highlighted weapon
                int idx = wheel.Selected;
                if (idx >= 0 && idx < wheelWeapons.Count) Function.Call(Hash.SET_CURRENT_PED_WEAPON, p2, (uint)wheelWeapons[idx], true);
                wheelOpen = false; wheel.Open = false; lbDownAt = -1;
            }
            return true;
        }
        if (!lb && lbWas && lbDownAt >= 0 && Game.GameTime - lbDownAt <= 220) CycleWeapon(-1);   // quick tap = previous weapon
        if (!lb) lbDownAt = -1;
        return false;
    }

    static string GameWeaponName(WeaponHash h)
    {
        try
        {
            string label = Weapon.GetDisplayNameFromHash(h);                       // e.g. WT_SMG_MCR
            string n = Function.Call<string>(Hash.GET_FILENAME_FOR_AUDIO_CONVERSATION, label);   // the game's own text for it
            if (!string.IsNullOrEmpty(n) && n != "NULL" && !n.StartsWith("WT_")) return n;
        }
        catch { }
        return WeaponLabel(h);
    }

    // ================================================================== Player 2's money
    int p2Money; bool moneyLoaded;
    HashSet<int> paidKills = new HashSet<int>();
    int lastKillScan;

    void LoadMoney()
    {
        if (moneyLoaded) return; moneyLoaded = true;
        try { p2Money = ScriptSettings.Load(System.IO.Path.Combine(Dir, "CoopPlus.ini")).GetValue("Player2Money", "Money", 500); } catch { p2Money = 500; }
    }
    void AddMoney(int amount, string why)
    {
        LoadMoney(); p2Money = Math.Max(0, p2Money + amount);
        Save("Player2Money", "Money", p2Money);
        if (amount > 0) Log("Player 2 earned $" + amount + " (" + why + ")");
    }

    // pays Player 2 for kills (like story-mode cash drops) - $20-$80 per kill
    void ScanKills()
    {
        if (Game.GameTime - lastKillScan < 400) return; lastKillScan = Game.GameTime;
        foreach (Ped q in World.GetNearbyPeds(p2.Position, 120f))
        {
            if (q == null || !q.Exists() || !q.IsDead || q == p2 || paidKills.Contains(q.Handle)) continue;
            int src = Function.Call<int>(Hash.GET_PED_SOURCE_OF_DEATH, q);
            Ped p1 = Game.Player.Character;
            if (src == p2.Handle || (p2.IsInVehicle() && src == p2.CurrentVehicle.Handle))
            {
                paidKills.Add(q.Handle);
                if (q == p1) { OnP2KilledP1(); continue; }
                AddMoney(20 + (q.Handle % 7) * 10, "kill");
                if (IsCop(q)) AddP2Wanted(2, "killed a cop");
                else if (World.GetNearbyPeds(q.Position, 50f).Any(c => c != null && c.Exists() && !c.IsDead && IsCop(c))) AddP2Wanted(1, "killed someone in front of the police");
                if (survival) survivalKills2++;
            }
            else if (src == p1.Handle) { paidKills.Add(q.Handle); if (survival) survivalKills1++; }
        }
        if (paidKills.Count > 500) paidKills.Clear();
    }

    // Player 2's cash, drawn where GTA draws the player's cash (Player 2's frames only)
    void DrawP2Money()
    {
        LoadMoney();
        Function.Call(Hash.HIDE_HUD_COMPONENT_THIS_FRAME, 3);   // Player 1's cash
        Function.Call(Hash.HIDE_HUD_COMPONENT_THIS_FRAME, 4);   // Player 1's cash change
        var te = new TextElement("$" + p2Money.ToString("N0"), new PointF(0.985f * GTA.UI.Screen.Width, 0.015f * GTA.UI.Screen.Height), 0.62f,
                                 Color.White, GTA.UI.Font.Pricedown, Alignment.Right, false, true);
        te.WrapWidth = 0.985f * GTA.UI.Screen.Width; te.Draw();
    }

    // ================================================================== shop (in Player 2's menu)
    static readonly Tuple<string, WeaponHash, int>[] ShopWeapons = {
        Tuple.Create("Pistol", WeaponHash.Pistol, 250), Tuple.Create("Combat Pistol", WeaponHash.CombatPistol, 500),
        Tuple.Create("Micro SMG", WeaponHash.MicroSMG, 900), Tuple.Create("SMG", WeaponHash.SMG, 1500),
        Tuple.Create("Pump Shotgun", WeaponHash.PumpShotgun, 1200), Tuple.Create("Assault Rifle", WeaponHash.AssaultRifle, 2500),
        Tuple.Create("Carbine Rifle", WeaponHash.CarbineRifle, 3000), Tuple.Create("Sniper Rifle", WeaponHash.SniperRifle, 4000),
        Tuple.Create("Grenade", WeaponHash.Grenade, 300), Tuple.Create("RPG", WeaponHash.RPG, 8000) };
    static readonly Tuple<string, string, int>[] ShopCars = {
        Tuple.Create("Sultan", "sultan", 1200), Tuple.Create("Bati 801 (bike)", "bati", 1500), Tuple.Create("Buffalo", "buffalo", 2000),
        Tuple.Create("Sanchez (dirt bike)", "sanchez", 800), Tuple.Create("Kuruma", "kuruma", 4000), Tuple.Create("Zentorno", "zentorno", 9000) };

    void BuildShopMenu()
    {
        menu.Items.Clear(); menu.ResetSelection(); menu.Title = "Shop"; menu.Sub = "PLAYER 2  $" + p2Money.ToString("N0");
        menu.Items.Add(new MenuItem { Label = "Body armour", Value = () => "$500", Help = "Full armour", Change = d => Buy(500, "armour", () => p2.Armor = 100) });
        menu.Items.Add(new MenuItem { Label = "First aid", Value = () => "$100", Help = "Full health", Change = d => Buy(100, "health", () => p2.Health = p2.MaxHealth) });
        foreach (var w in ShopWeapons) { var ww = w; menu.Items.Add(new MenuItem { Label = ww.Item1, Value = () => "$" + ww.Item3.ToString("N0"), Change = d => Buy(ww.Item3, ww.Item1, () => p2.Weapons.Give(ww.Item2, 500, true, true)) }); }
        foreach (var c in ShopCars) { var cc = c; menu.Items.Add(new MenuItem { Label = cc.Item1, Value = () => "$" + cc.Item3.ToString("N0"), Help = "Delivered next to Player 2", Change = d => Buy(cc.Item3, cc.Item1, () => DeliverCar(cc.Item2)) }); }
        menu.Items.Add(new MenuItem { Label = "Back", Change = d => { BuildMenu(); } });
    }

    void Buy(int price, string what, Action give)
    {
        LoadMoney();
        if (!Alive(p2)) return;
        if (p2Money < price) { Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET", true); return; }
        AddMoney(-price, "bought " + what); give();
        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "PURCHASE", "HUD_LIQUOR_STORE_SOUNDSET", true);
        Log("Player 2 bought " + what + " for $" + price);
        menu.Sub = "PLAYER 2  $" + p2Money.ToString("N0");
    }

    void DeliverCar(string model)
    {
        Vector3 at = p2.GetOffsetPosition(new Vector3(3.5f, 2f, 0f));
        Vehicle v = World.CreateVehicle(new Model(model), at, p2.Heading);
        if (v != null) { v.PlaceOnGround(); v.MarkAsNoLongerNeeded(); }
    }

    // ================================================================== revive (Player 1 presses E next to Player 2)
    const int ReviveWindowMs = 12000;
    bool CanRevive(Ped p1) { return Alive(p1) && p2 != null && p2.Exists() && p2.IsDead && p1.Position.DistanceTo(p2.Position) < 3f; }

    void TryRevive()
    {
        Ped p1 = Game.Player.Character;
        if (!CanRevive(p1) || deadSince < 0) return;
        Vector3 pos = p2.Position; float h = p2.Heading;
        Function.Call(Hash.RESURRECT_PED, p2);
        Function.Call(Hash.REVIVE_INJURED_PED, p2);
        Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, p2);
        p2.Position = pos; p2.Heading = h; p2.Health = 200; p2.Armor = 0;
        deadSince = -1; taskKind = "";
        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "CHECKPOINT_PERFECT", "HUD_MINI_GAME_SOUNDSET", true);
        Log("Player 1 revived Player 2");
    }

    void DrawReviveHint()
    {
        Ped p1 = Game.Player.Character;
        if (!CanRevive(p1)) return;
        Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, "Press ~INPUT_CONTEXT~ to revive Player 2.");
        Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);
    }

    // ================================================================== activities
    bool deathmatch, survival, race;
    int dm1, dm2; const int DmTarget = 10;
    int survivalWave, survivalKills1, survivalKills2, nextWaveAt; List<Ped> survivalPeds = new List<Ped>(); int survivalGroup;
    Vector3 raceFinish; Blip raceBlip; int raceCheckpoint = -1, raceStart; string raceWinner;
    int lastP1Dead;
    int bigMsg; string bigTitle, bigSub; int bigUntil;

    void ShowBig(string title, string sub, int ms)
    {
        bigMsg = Function.Call<int>(Hash.REQUEST_SCALEFORM_MOVIE, "MP_BIG_MESSAGE_FREEMODE");
        bigTitle = title; bigSub = sub; bigUntil = Game.GameTime + ms; bigShown = false;
        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "MP_WAVE_COMPLETE", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
    }
    bool bigShown;
    void DrawBig()
    {
        if (Game.GameTime > bigUntil || bigMsg == 0 || !Function.Call<bool>(Hash.HAS_SCALEFORM_MOVIE_LOADED, bigMsg)) return;
        if (!bigShown)
        {
            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, bigMsg, "SHOW_SHARD_CENTERED_MP_MESSAGE");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_TEXTURE_NAME_STRING, bigTitle);
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_TEXTURE_NAME_STRING, bigSub);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);
            bigShown = true;
        }
        Function.Call(Hash.DRAW_SCALEFORM_MOVIE_FULLSCREEN, bigMsg, 255, 255, 255, 255, 0);
    }

    void BuildActivitiesMenu()
    {
        menu.Items.Clear(); menu.ResetSelection(); menu.Title = "Activities"; menu.Sub = "CO-OP";
        menu.Items.Add(new MenuItem { Label = "Deathmatch", Value = () => OnOff(deathmatch), Help = "First to " + DmTarget + " kills on each other wins", Change = d => { StopActivities(); if (d != 0 || true) { deathmatch = true; dm1 = dm2 = 0; friendlyFire = true; ApplyP2Flags(); ShowBig("DEATHMATCH", "First to " + DmTarget, 3000); Log("deathmatch started"); } } });
        menu.Items.Add(new MenuItem { Label = "Survival", Value = () => OnOff(survival), Help = "Fight off waves of enemies together", Change = d => { StopActivities(); StartSurvival(); } });
        menu.Items.Add(new MenuItem { Label = "Race", Value = () => OnOff(race), Help = "Race to your map waypoint (or a spot ~1 km away)", Change = d => { StopActivities(); StartRace(); } });
        menu.Items.Add(new MenuItem { Label = "Stop activity", Change = d => { StopActivities(); } });
        menu.Items.Add(new MenuItem { Label = "Back", Change = d => { BuildMenu(); } });
    }

    void StopActivities()
    {
        if (deathmatch || survival || race) Log("activity stopped");
        deathmatch = false; survival = false; race = false;
        foreach (Ped q in survivalPeds) if (q != null && q.Exists()) q.MarkAsNoLongerNeeded();
        survivalPeds.Clear();
        if (raceBlip != null && raceBlip.Exists()) raceBlip.Delete();
        if (raceCheckpoint >= 0) { Function.Call(Hash.DELETE_CHECKPOINT, raceCheckpoint); raceCheckpoint = -1; }
    }

    void OnP2KilledP1() { if (deathmatch) { dm2++; Log("deathmatch: Player 2 scores (" + dm1 + "-" + dm2 + ")"); CheckDmWin(); } }
    void OnP1KilledP2() { if (deathmatch) { dm1++; Log("deathmatch: Player 1 scores (" + dm1 + "-" + dm2 + ")"); CheckDmWin(); } }
    void CheckDmWin()
    {
        if (dm1 >= DmTarget) { ShowBig("WINNER", "Player 1 wins " + dm1 + "-" + dm2, 6000); deathmatch = false; }
        else if (dm2 >= DmTarget) { ShowBig("WINNER", "Player 2 wins " + dm2 + "-" + dm1, 6000); deathmatch = false; }
    }

    void StartSurvival()
    {
        survival = true; survivalWave = 0; survivalKills1 = survivalKills2 = 0; nextWaveAt = Game.GameTime + 3000;
        if (survivalGroup == 0)
        {
            OutputArgument g = new OutputArgument();
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "COOPPLUS_SURVIVAL", g); survivalGroup = g.GetResult<int>();
        }
        int pg = Game.Player.Character.RelationshipGroup.Hash;
        Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, survivalGroup, pg); Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, pg, survivalGroup);
        Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, survivalGroup, p2Group); Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, p2Group, survivalGroup);
        ShowBig("SURVIVAL", "Survive the waves", 3000);
        Log("survival started");
    }

    static readonly string[] SurvivalModels = { "g_m_y_ballaorig_01", "g_m_y_mexgoon_01", "g_m_y_lost_01", "g_m_y_salvagoon_01" };
    static readonly WeaponHash[] SurvivalGuns = { WeaponHash.Pistol, WeaponHash.MicroSMG, WeaponHash.PumpShotgun, WeaponHash.AssaultRifle };

    void UpdateSurvival(Ped p1)
    {
        survivalPeds.RemoveAll(q => q == null || !q.Exists() || q.IsDead);
        if (survivalPeds.Count == 0 && Game.GameTime > nextWaveAt)
        {
            if (survivalWave > 0) { ShowBig("WAVE " + survivalWave + " COMPLETE", "Player 1: " + survivalKills1 + "   Player 2: " + survivalKills2, 3500); AddMoney(250 * survivalWave, "survival wave"); nextWaveAt = Game.GameTime + 6000; survivalWave++; return; }
            survivalWave = Math.Max(1, survivalWave);
            Vector3 mid = (p1.Position + p2.Position) * 0.5f;
            int n = 3 + survivalWave * 2;
            for (int i = 0; i < n; i++)
            {
                double ang = i * Math.PI * 2 / n; float r = 45f + (i % 3) * 8f;
                Vector3 at = World.GetNextPositionOnStreet(mid + new Vector3((float)Math.Cos(ang) * r, (float)Math.Sin(ang) * r, 0));
                Ped q = World.CreatePed(new Model(SurvivalModels[i % SurvivalModels.Length]), at);
                if (q == null) continue;
                q.Weapons.Give(SurvivalGuns[Math.Min(SurvivalGuns.Length - 1, (survivalWave - 1) / 2 + i % 2)], 999, true, true);
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, q, survivalGroup);
                q.Accuracy = 20 + survivalWave * 4; q.IsPersistent = true;
                Function.Call(Hash.TASK_COMBAT_HATED_TARGETS_AROUND_PED, q, 300f, 0);
                q.AddBlip().Color = BlipColor.Red;
                survivalPeds.Add(q);
            }
            Log("survival wave " + survivalWave + " (" + survivalPeds.Count + " enemies)");
            nextWaveAt = Game.GameTime + 2000;
        }
    }

    void StartRace()
    {
        Ped p1 = Game.Player.Character;
        Vector3 wp = World.WaypointPosition;
        if (wp == Vector3.Zero)
        {
            Vector3 fwd = p1.ForwardVector; fwd.Z = 0; fwd.Normalize();
            wp = World.GetNextPositionOnStreet(p1.Position + fwd * 1000f);
        }
        else wp.Z = World.GetGroundHeight(new Vector2(wp.X, wp.Y));
        raceFinish = wp; race = true; raceWinner = null; raceStart = Game.GameTime;
        raceBlip = World.CreateBlip(wp); raceBlip.Sprite = BlipSprite.RaceFinish; raceBlip.ShowRoute = true;
        raceCheckpoint = Function.Call<int>(Hash.CREATE_CHECKPOINT, 4, wp.X, wp.Y, wp.Z + 1f, wp.X, wp.Y, wp.Z, 12f, 255, 200, 0, 150, 0);
        ShowBig("RACE", "First to the finish wins", 3000);
        Log("race started, finish " + (int)p1.Position.DistanceTo(wp) + " m away");
    }

    void UpdateRace(Ped p1)
    {
        if (raceWinner != null) return;
        string w = null;
        if (p1.Position.DistanceTo2D(raceFinish) < 12f) w = "Player 1";
        else if (Alive(p2) && p2.Position.DistanceTo2D(raceFinish) < 12f) w = "Player 2";
        if (w == null) return;
        raceWinner = w; int secs = (Game.GameTime - raceStart) / 1000;
        ShowBig("WINNER", w + " wins in " + secs + "s", 6000);
        if (w == "Player 2") AddMoney(1000, "race win");
        Log("race won by " + w);
        race = false;
        if (raceBlip != null && raceBlip.Exists()) raceBlip.Delete();
        if (raceCheckpoint >= 0) { Function.Call(Hash.DELETE_CHECKPOINT, raceCheckpoint); raceCheckpoint = -1; }
    }

    // scoreboard text in GTA's own HUD font, top centre, on both screens
    void DrawActivityHud()
    {
        string s = null;
        if (deathmatch) s = "Player 1  " + dm1 + "  -  " + dm2 + "  Player 2";
        else if (survival) s = "Wave " + survivalWave + "   Enemies left " + survivalPeds.Count;
        else if (race && Alive(p2)) s = "Finish: Player 1 " + (int)Game.Player.Character.Position.DistanceTo2D(raceFinish) + " m   Player 2 " + (int)p2.Position.DistanceTo2D(raceFinish) + " m";
        if (s == null) return;
        var te = new TextElement(s, new PointF(0.5f * GTA.UI.Screen.Width, 0.03f * GTA.UI.Screen.Height), 0.5f, Color.White, GTA.UI.Font.ChaletLondon, Alignment.Center, true, true);
        te.Draw();
    }

    // ================================================================== called from the main loop
    void UpdateFeatures(Ped p1)
    {
        if (Alive(p2)) ScanKills();
        // Player 1 killed by Player 2 (deathmatch scoring)
        if (deathmatch && p1 != null && p1.Exists() && p1.IsDead && Game.GameTime - lastP1Dead > 8000)
        {
            lastP1Dead = Game.GameTime;
            int src = Function.Call<int>(Hash.GET_PED_SOURCE_OF_DEATH, p1);
            if (p2 != null && src == p2.Handle) OnP2KilledP1();
        }
        if (survival && Alive(p1) && Alive(p2)) UpdateSurvival(p1);
        if (race && Alive(p1)) UpdateRace(p1);
    }

    void P2Died()
    {
        if (!deathmatch || p2 == null) return;
        int src = Function.Call<int>(Hash.GET_PED_SOURCE_OF_DEATH, p2);
        Ped p1 = Game.Player.Character;
        if (src == p1.Handle || (p1.IsInVehicle() && src == p1.CurrentVehicle.Handle)) OnP1KilledP2();
    }

    // Player 2's controller rumbles when they get hit (Xbox controllers)
    void Rumble(float strength, int ms)
    {
        if (padSlot < 0 || padSlot >= DirectPads.SlotBase) return;
        xinput.Vibrate(padSlot, strength, ms);
    }

    bool FeatureKey(Keys k)
    {
        if (k == Keys.E && Alive(Game.Player.Character) && p2 != null && p2.Exists() && p2.IsDead && CanRevive(Game.Player.Character)) { TryRevive(); return true; }
        return false;
    }
}
