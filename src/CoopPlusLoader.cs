// CoopPlusLoader - the only part Script Hook V .NET loads (scripts\CoopPlus.dll).
// It loads the real logic from <GTA>\CoopPlus\core\CoopPlusCore.dll and reloads it within about a second
// whenever that file changes, so the mod can be updated while the game keeps running (no restart,
// no reloading Story Mode). Player 2 carries over through a reload.
using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using GTA;

public class CoopPlusLoader : Script
{
    static readonly string Dir = Path.Combine(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName), "CoopPlus");
    readonly string corePath = Path.Combine(Dir, "core", "CoopPlusCore.dll");
    DateTime loadedStamp; int lastCheck;
    Action tick; Action<KeyEventArgs> key; Action<bool> unload;

    public CoopPlusLoader()
    {
        Interval = 0;
        // the core is loaded from bytes, so point its references at the assemblies already loaded here
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) =>
        {
            string want = new AssemblyName(a.Name).Name;
            foreach (Assembly x in AppDomain.CurrentDomain.GetAssemblies())
                if (x.GetName().Name == want) return x;
            return null;
        };
        Tick += (s, e) =>
        {
            if (Environment.TickCount - lastCheck > 1000) { lastCheck = Environment.TickCount; CheckForNewCore(); }
            if (tick != null) tick();
        };
        KeyDown += (s, e) => { if (key != null) key(e); };
        Aborted += (s, e) => Unload(false);
    }

    void CheckForNewCore()
    {
        DateTime stamp;
        try { if (!File.Exists(corePath)) return; stamp = File.GetLastWriteTimeUtc(corePath); } catch { return; }
        if (stamp == loadedStamp || (DateTime.UtcNow - stamp).TotalMilliseconds < 600) return;   // unchanged, or still being copied
        bool reloading = tick != null;
        Unload(reloading);
        loadedStamp = stamp;
        try
        {
            Assembly asm = Assembly.Load(File.ReadAllBytes(corePath));   // a fresh copy each time, so it can be replaced
            object core = Activator.CreateInstance(asm.GetType("CoopCore", true));
            tick = (Action)Delegate.CreateDelegate(typeof(Action), core, "Tick");
            key = (Action<KeyEventArgs>)Delegate.CreateDelegate(typeof(Action<KeyEventArgs>), core, "Key");
            unload = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), core, "Unload");
            Note((reloading ? "core reloaded " : "core loaded ") + stamp.ToLocalTime().ToString("HH:mm:ss"));
        }
        catch (Exception e) { tick = null; key = null; unload = null; Note("core failed to load: " + e); }
    }

    void Unload(bool reloading)
    {
        try { if (unload != null) unload(reloading); } catch (Exception e) { Note("core unload error: " + e.Message); }
        tick = null; key = null; unload = null;
    }

    static void Note(string m)
    {
        try { File.AppendAllText(Path.Combine(Dir, "CoopPlus.log"), DateTime.Now.ToString("[HH:mm:ss] ") + "[loader] " + m + Environment.NewLine); } catch { }
    }
}
