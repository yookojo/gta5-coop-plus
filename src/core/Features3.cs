// Features3.cs - reliable keys (read directly), key rebinding, controller set-up wizard / button remapping,
// one-monitor auto layout, first-run hint.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Native;

public partial class CoopCore
{
    // ================================================================== keys, read directly every frame
    // Script Hook V .NET doesn't always pass key presses on, so the keyboard is read straight from Windows,
    // only while GTA is the active window.
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    readonly bool[] keyDown = new bool[256];
    Action<Keys> keyCapture;            // set while waiting for "press a key"

    static bool GameFocused() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid == MyPid; }

    void PollKeys()
    {
        if (!GameFocused()) { for (int i = 0; i < 256; i++) keyDown[i] = false; return; }
        for (int vk = 8; vk < 255; vk++)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool pressed = down && !keyDown[vk];
            keyDown[vk] = down;
            if (!pressed) continue;
            if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk >= 0xA0 && vk <= 0xA5) continue;   // shift/ctrl/alt on their own
            Keys k = (Keys)vk;
            if (keyCapture != null) { var cb = keyCapture; keyCapture = null; if (k != Keys.Escape) cb(k); else Notify("Cancelled"); continue; }
            OnKeyDown(null, new KeyEventArgs(k));
        }
    }

    // ================================================================== rebinding Player 1's co-op keys
    void BuildKeysMenu()
    {
        menu.Items.Clear(); menu.ResetSelection(); menu.Title = "Keys"; menu.Sub = "PLAYER 1";
        Action<string, string, Func<Keys>, Action<Keys>> bind = (label, iniKey, get, set) => menu.Items.Add(new MenuItem
        {
            Label = label, Help = "Select, then press the new key (Esc cancels)", Value = () => get().ToString(),
            Change = d => { Notify("Press the new key for " + label); keyCapture = k => { set(k); Save("Keys", iniKey, k); Notify(label + ": " + k); }; }
        });
        bind("Co-op menu", "Menu", () => menuKey, k => menuKey = k);
        bind("Add / remove Player 2", "SpawnPlayer2", () => spawnKey, k => spawnKey = k);
        bind("Split screen on/off", "SplitScreen", () => splitKey, k => splitKey = k);
        bind("Swap screens", "SwapScreens", () => swapKey, k => swapKey = k);
        bind("Ride together", "RideTogether", () => rideKey, k => rideKey = k);
        menu.Items.Add(new MenuItem { Label = "Back", Change = d => BuildMenu() });
    }

    // ================================================================== controller set-up wizard / remapping
    // Every controller is read into one common layout (Xbox names). A per-controller map then says which
    // physical button acts as which logical one, so any controller can be set up the way the player likes.
    static readonly Tuple<int, string>[] RemapSteps = {
        Tuple.Create(Pad.A, "Sprint"), Tuple.Create(Pad.X, "Jump / climb"), Tuple.Create(Pad.Y, "Get in / out of a car"),
        Tuple.Create(Pad.B, "Reload / cancel"), Tuple.Create(Pad.LB, "Weapon wheel"), Tuple.Create(Pad.RB, "Next weapon"),
        Tuple.Create(Pad.LS, "Crouch"), Tuple.Create(Pad.RS, "First person / look back"), Tuple.Create(Pad.Back, "Co-op menu"),
        Tuple.Create(Pad.Start, "Join / respawn") };
    Dictionary<string, Dictionary<int, int>> remaps = new Dictionary<string, Dictionary<int, int>>();   // device -> (physical bit -> logical bit)
    int wizardStep = -1; Dictionary<int, int> wizardMap; string wizardDevice; int wizardArmAt;

    string DeviceId(int slot) { return slot >= DirectPads.SlotBase ? dpads.DeviceId(slot) : "xinput"; }

    Dictionary<int, int> RemapFor(int slot)
    {
        string id = DeviceId(slot); Dictionary<int, int> m;
        if (remaps.TryGetValue(id, out m)) return m;
        m = new Dictionary<int, int>();
        try
        {
            var ini = ScriptSettings.Load(System.IO.Path.Combine(Dir, "CoopPlus.ini"));
            string s = ini.GetValue("Remap", id, "");
            foreach (string pair in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            { var kv = pair.Split(':'); m[Convert.ToInt32(kv[0], 16)] = Convert.ToInt32(kv[1], 16); }
        }
        catch { }
        remaps[id] = m; return m;
    }

    Pad ApplyRemap(Pad p, int slot)
    {
        var m = RemapFor(slot); if (m.Count == 0) return p;
        int outB = 0;
        for (int bit = 1; bit <= 0x8000; bit <<= 1)
        {
            if ((p.Buttons & bit) == 0) continue;
            int to; outB |= m.TryGetValue(bit, out to) ? to : bit;
        }
        p.Buttons = outB; return p;
    }

    void StartWizard()
    {
        wizardDevice = DeviceId(padSlot); wizardMap = new Dictionary<int, int>(); wizardStep = 0; wizardArmAt = Game.GameTime + 600;
        menu.Hide(); Notify("Controller set-up: press the button you want for each action");
    }

    // runs before Player 2's controls while the wizard is active; uses the controller's raw (un-remapped) buttons
    bool WizardInput(Pad rawUnmapped, Pad prevUnmapped)
    {
        if (wizardStep < 0) return false;
        if (Game.GameTime < wizardArmAt) return true;
        int pressed = rawUnmapped.Buttons & ~prevUnmapped.Buttons;
        if (pressed != 0)
        {
            int bit = pressed & -pressed;                          // lowest newly pressed button
            wizardMap[bit] = RemapSteps[wizardStep].Item1;
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
            wizardStep++; wizardArmAt = Game.GameTime + 250;
            if (wizardStep >= RemapSteps.Length)
            {
                remaps[wizardDevice] = wizardMap;
                Save("Remap", wizardDevice, string.Join(",", wizardMap.Select(kv => kv.Key.ToString("X") + ":" + kv.Value.ToString("X"))));
                Log("controller remapped (" + wizardDevice + "): " + string.Join(", ", wizardMap.Select(kv => kv.Key.ToString("X") + "->" + kv.Value.ToString("X"))));
                Notify("Controller set up"); wizardStep = -1;
            }
        }
        return true;
    }

    void DrawWizard()
    {
        if (wizardStep < 0) return;
        Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, "Controller set-up (" + (wizardStep + 1) + "/" + RemapSteps.Length + "): press the button for ~y~" + RemapSteps[wizardStep].Item2 + "~s~.");
        Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);
    }

    void ResetRemap()
    {
        string id = DeviceId(padSlot); remaps[id] = new Dictionary<int, int>(); Save("Remap", id, ""); Notify("Controller buttons back to default");
    }

    // ================================================================== one monitor / first run
    bool screensChecked;
    void CheckScreensOnce()
    {
        if (screensChecked) return; screensChecked = true;
        int n = Screen.AllScreens.Length;
        if (n < 2 && layout == 0) { layout = 1; Log("only one screen found - using side by side"); }
        Notify("Co-op ready: ~b~" + menuKey + "~s~ menu, ~b~" + spawnKey + "~s~ add Player 2" + (n < 2 ? " (one screen: side by side)" : ""));
    }
}
