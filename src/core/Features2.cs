// Features2.cs - Player 2's own wanted level, flying, first person, ride together, frame share,
// identify screens, saved loadout/outfit/car, shared mission payouts.
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
    // ================================================================== frame share
    int frameShare;   // 0 equal, 1 Player 1 gets 2 of every 3 frames, 2 Player 2 gets 2 of every 3
    static readonly string[] FrameShareNames = { "Equal", "More for Player 1", "More for Player 2" };
    int ViewFor(int frame)
    {
        if (frameShare == 1) return frame % 3 == 2 ? 2 : 1;
        if (frameShare == 2) return frame % 3 == 2 ? 1 : 2;
        return frame % 2 == 0 ? 1 : 2;
    }

    // ================================================================== identify screens
    int identifyUntil;
    void DrawIdentify(int whose)
    {
        if (Game.GameTime > identifyUntil) return;
        var te = new TextElement(whose == 2 ? "PLAYER 2" : "PLAYER 1", new PointF(0.5f * GTA.UI.Screen.Width, 0.42f * GTA.UI.Screen.Height),
                                 1.6f, Color.White, GTA.UI.Font.Pricedown, Alignment.Center, true, true);
        te.Draw();
    }

    // ================================================================== first person (RS click on foot)
    bool firstPerson;
    Vector3 FirstPersonPos() { return p2.Bones[Bone.SkelHead].Position + camFwd * 0.18f + new Vector3(0, 0, 0.05f); }

    // ================================================================== ride together (Player 1: G)
    void RideTogether()
    {
        Ped p1 = Game.Player.Character;
        if (!Alive(p1) || !Alive(p2)) return;
        if (p2.IsInVehicle() && !p1.IsInVehicle())
        {
            Vehicle v = p2.CurrentVehicle;
            int seats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, v);
            for (int i = 0; i < seats; i++) if (v.IsSeatFree((VehicleSeat)i)) { p1.Task.EnterVehicle(v, (VehicleSeat)i, 8000, 2f, EnterVehicleFlags.ResumeIfInterupted); Log("ride together: Player 1 -> Player 2's car"); return; }
        }
        else if (p1.IsInVehicle()) { TeleportToP1(p1); Log("ride together: Player 2 -> Player 1's car"); }
    }

    // ================================================================== flying (helicopters and planes)
    int lastFlyTask;
    bool Fly(Vehicle v)
    {
        bool heli = v.Model.IsHelicopter, plane = v.Model.IsPlane;
        if (!heli && !plane) return false;
        if (Game.GameTime - lastFlyTask < 350) return true;
        lastFlyTask = Game.GameTime;
        Vector3 fwd = new Vector3(camFwd.X, camFwd.Y, 0).Normalized, right = Vector3.Cross(fwd, Vector3.WorldUp).Normalized;
        if (heli)
        {   // left stick moves, RT up, LT down; let go of everything to hover
            Vector3 t = v.Position + fwd * (40f * pad.LY) + right * (40f * pad.LX) + Vector3.WorldUp * (15f * (pad.RT - pad.LT));
            Function.Call(Hash.TASK_HELI_MISSION, p2, v, 0, 0, t.X, t.Y, t.Z, 4, 25f + 25f * Math.Abs(pad.LY), 2f, NormAngle(camYaw), -1, -1, -1f, 0);
        }
        else
        {   // planes: RT throttle, left stick steers and climbs
            float throttle = Math.Max(0.15f, pad.RT);
            Vector3 dir = Forward(camYaw - pad.LX * 25f, Clamp(camPitch + pad.LY * 25f, -35f, 45f));
            Vector3 t = v.Position + dir * 600f;
            Function.Call(Hash.TASK_PLANE_MISSION, p2, v, 0, 0, t.X, t.Y, t.Z, 4, 30f + 70f * throttle, 0f, NormAngle(camYaw), 2000f, 30f, 0);
        }
        taskKind = "fly";
        return true;
    }

    // ================================================================== Player 2's own wanted level
    int p2Wanted, lastWantedTick, lastSeenByCops;
    List<Ped> p2Cops = new List<Ped>();

    void AddP2Wanted(int n, string why)
    {
        int before = p2Wanted;
        p2Wanted = Math.Min(5, p2Wanted + n); lastSeenByCops = Game.GameTime;
        if (p2Wanted != before) { Log("Player 2 wanted level " + p2Wanted + " (" + why + ")"); Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "Bomb_Disarmed", "GTAO_Speed_Convoy_Soundset", true); }
    }

    static bool IsCop(Ped q)
    {
        int t = Function.Call<int>(Hash.GET_PED_TYPE, q);
        return t == 6 || t == 27 || t == 29;   // cop, swat, army
    }

    void UpdateP2Wanted()
    {
        if (Game.GameTime - lastWantedTick < 1000) return; lastWantedTick = Game.GameTime;
        p2Cops.RemoveAll(c => c == null || !c.Exists() || c.IsDead);
        if (p2Wanted == 0) { foreach (Ped c in p2Cops) c.MarkAsNoLongerNeeded(); p2Cops.Clear(); return; }
        // cops who can see Player 2 keep the heat on; out of sight for long enough and a star drops
        bool seen = World.GetNearbyPeds(p2.Position, 70f).Any(q => q != null && q.Exists() && !q.IsDead && IsCop(q)
                    && Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, q, p2, 17));
        if (seen) lastSeenByCops = Game.GameTime;
        if (Game.GameTime - lastSeenByCops > 12000 + 3000 * p2Wanted) { p2Wanted--; lastSeenByCops = Game.GameTime; Log("Player 2 wanted level " + p2Wanted + " (lost the cops)"); }
        // police units chase Player 2: more stars, more cops
        int want = Math.Min(8, p2Wanted * 2);
        if (p2Cops.Count < want && !p2.IsInVehicle() || p2Cops.Count < want / 2)
        {
            Vector3 at = World.GetNextPositionOnStreet(p2.Position + Forward(camYaw + 180f + (p2Cops.Count * 47) % 120 - 60, 0) * 70f);
            Vehicle car = World.CreateVehicle(new Model(p2Wanted >= 4 ? "fbi2" : "police3"), at);
            for (int i = 0; i < 2 && car != null; i++)
            {
                Ped c = World.CreatePed(new Model(p2Wanted >= 4 ? "s_m_y_swat_01" : "s_m_y_cop_01"), at + new Vector3(i * 2, 0, 0));
                if (c == null) continue;
                c.SetIntoVehicle(car, i == 0 ? VehicleSeat.Driver : VehicleSeat.Passenger);
                c.Weapons.Give(p2Wanted >= 3 ? WeaponHash.CarbineRifle : WeaponHash.Pistol, 500, true, true);
                c.IsPersistent = true;
                Function.Call(Hash.TASK_COMBAT_PED, c, p2, 0, 16);
                p2Cops.Add(c);
            }
            if (car != null) car.MarkAsNoLongerNeeded();
        }
    }

    // GTA's own wanted-star icon (~ws~), top right under Player 2's cash, on Player 2's frames
    void DrawP2Wanted()
    {
        Function.Call(Hash.HIDE_HUD_COMPONENT_THIS_FRAME, 1);   // Player 1's stars
        if (p2Wanted <= 0) return;
        var te = new TextElement(string.Concat(Enumerable.Repeat("~ws~", p2Wanted)), new PointF(0.985f * GTA.UI.Screen.Width, 0.058f * GTA.UI.Screen.Height),
                                 0.62f, Color.White, GTA.UI.Font.ChaletLondon, Alignment.Right, false, true);
        te.WrapWidth = 0.985f * GTA.UI.Screen.Width; te.Draw();
    }

    // ================================================================== loadout, outfit, my car
    void SaveLoadout()
    {
        if (!Alive(p2)) return;
        var have = AllWeapons.Where(h => h != WeaponHash.Unarmed && Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, p2, (uint)h, false)).Select(h => ((uint)h).ToString());
        Save("Player2Loadout", "Weapons", string.Join(",", have));
    }
    void RestoreLoadout()
    {
        try
        {
            string s = ScriptSettings.Load(System.IO.Path.Combine(Dir, "CoopPlus.ini")).GetValue("Player2Loadout", "Weapons", "");
            foreach (string x in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) p2.Weapons.Give((WeaponHash)(int)uint.Parse(x), 250, false, true);
        }
        catch (Exception e) { Log("loadout restore failed: " + e.Message); }
    }
    int lastLoadoutSave; string myCar = "";
    void NoteMyCar()
    {
        Vehicle v = p2.CurrentVehicle;
        if (v == null || v.Driver != p2) return;
        string m = v.Model.Hash.ToString();
        if (m != myCar) { myCar = m; Save("Player2", "MyCar", m); }
    }
    void DeliverMyCar()
    {
        if (string.IsNullOrEmpty(myCar)) myCar = ScriptSettings.Load(System.IO.Path.Combine(Dir, "CoopPlus.ini")).GetValue("Player2", "MyCar", "");
        int h; if (!int.TryParse(myCar, out h)) return;
        Vehicle v = World.CreateVehicle(new Model(h), p2.GetOffsetPosition(new Vector3(3.5f, 2f, 0f)), p2.Heading);
        if (v != null) { v.PlaceOnGround(); v.MarkAsNoLongerNeeded(); Log("delivered Player 2's car"); }
    }

    // ================================================================== shared mission payouts
    int lastP1Money = int.MinValue;
    void ShareBigPayouts()
    {
        int m = Game.Player.Money;
        if (lastP1Money != int.MinValue && m - lastP1Money >= 1000) AddMoney(m - lastP1Money, "share of Player 1's payout");
        lastP1Money = m;
    }

    // ================================================================== per-tick hook
    void UpdateFeatures2(Ped p1)
    {
        if (!Alive(p2)) return;
        UpdateP2Wanted();
        NoteMyCar();
        if (Game.GameTime - lastLoadoutSave > 30000) { lastLoadoutSave = Game.GameTime; SaveLoadout(); }
        if (Game.FrameCount % 30 == 0) ShareBigPayouts();
        if (!p2.IsInVehicle() && Pressed(Pad.RS)) { firstPerson = !firstPerson; Log("Player 2 first person " + firstPerson); }
    }
}
