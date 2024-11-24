using UnityEngine;
using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ukagaka {
    [BepInPlugin("io.github.tatakinov.ukagaka_for_elin", "Ukagaka for Elin", "1.0.0.0")]
    public class Mod_Ukagaka : BaseUnityPlugin {
        private void Start() {
            var harmony = new Harmony("UkagakaForElin");
            harmony.PatchAll();
            System.Threading.Tasks.Task.Run(() => {
                SSTPClient client = SSTPClient.GetInstance();
                var _ = client.StartServer();
            });
        }
    }

    class SSTPClient {
        private static SSTPClient _instance = new SSTPClient();

        private BlockingCollection<string> _bc;

        private SSTPClient() {
            _bc = new BlockingCollection<string>();
        }

        public static SSTPClient GetInstance() {
            return _instance;
        }

        public void Enqueue(string event_name, params object[] args) {
            string message = $"NOTIFY SSTP/1.1\r\nCharset: UTF-8\r\nSender: Elin\r\nEvent: {event_name}\r\n";
            for (int i = 0; i < args.Length; i++) {
                message += $"Reference{i}: {args[i].ToString()}\r\n";
            }
            message += "\r\n";
            _bc.TryAdd(message, 0);
        }

        public async System.Threading.Tasks.Task StartServer() {
            try {
                while (true) {
                    string message = _bc.Take();
                    try {
                        using Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        await socket.ConnectAsync("localhost", 9801);
                        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                        int sent = 0;
                        while (sent < messageBytes.Length) {
                            sent += await socket.SendAsync(messageBytes[sent..messageBytes.Length], SocketFlags.None);
                        }
                        byte[] buffer = new byte[1024];
                        while (true) {
                            int received = await socket.ReceiveAsync(buffer, SocketFlags.None);
                            if (received == 0) {
                                break;
                            }
                        }
                    }
                    catch {
                        // 失敗したらしたでいいのでnop
                    }
                }
            }
            catch (InvalidOperationException) {
                // nop
            }
        }

        public void StopServer() {
            _bc.CompleteAdding();
        }
    }

    [HarmonyPatch]
    class CorePatch {
        [HarmonyPatch(typeof(Core)), HarmonyPatch(nameof(Core.Quit)), HarmonyPrefix]
        public static void Prefix() {
            SSTPClient client = SSTPClient.GetInstance();
            client.StopServer();
        }
    }

    [HarmonyPatch]
    class CharaAndThingPatch {
        private static bool IsInZoneActivate = false;

        private static string PackChara(Chara c) {
            string ret = c.Name;
            List<string> things = PackThing(c);
            if (things.Count > 0) {
                ret += "\x02" + string.Join("\x02", things);
            }
            Debug.Log("PackChara: " + ret);
            return ret;
        }

        private static List<string> PackThing(Card c) {
            List<string> ret = new List<string>();
            if (c.isThing) {
                // 一時的に識別済みにして名前を取得する
                int bak = c.c_IDTState;
                c.c_IDTState = 0;
                ret.Add(c.Name);
                c.c_IDTState = bak;
            }
            foreach (Thing t in c.things) {
                ret.AddRange(PackThing(t));
            }
            return ret;
        }

        [HarmonyPatch(typeof(Zone)), HarmonyPatch(nameof(Zone.Activate)), HarmonyPrefix]
        public static void ZoneActivatePrefix() {
            IsInZoneActivate = true;
        }

        [HarmonyPatch(typeof(Zone)), HarmonyPatch(nameof(Zone.Activate)), HarmonyPostfix]
        public static void ZoneActivatePostfix(Zone __instance) {
            Debug.Log("Zone.Activate");
            Dictionary<Hostility, List<string>> chara = new Dictionary<Hostility, List<string>>();
            List<string> thing = new List<string>();
            SSTPClient client = SSTPClient.GetInstance();

            IsInZoneActivate = false;

            foreach (Hostility h in Enum.GetValues(typeof(Hostility))) {
                chara[h] = new List<string>();
            }

            foreach (Card c in __instance.map.Cards) {
                if (c.isChara) {
                    Hostility h = c.Chara.OriginalHostility;
                    chara[h].Add(PackChara(c.Chara));
                }
            }
            __instance.map.ForeachCell(c => {
                foreach (Thing t in c.Things) {
                    thing.AddRange(PackThing(t));
                }
            });
            Debug.Log(string.Join(" ", thing));
            client.Enqueue("OnElinMapEnter",
                    __instance.Name,
                    string.Join("\x01", chara[Hostility.Enemy]),
                    string.Join("\x01", chara[Hostility.Neutral]),
                    string.Join("\x01", chara[Hostility.Friend]),
                    string.Join("\x01", chara[Hostility.Ally]),
                    string.Join("\x01", thing),
                    __instance.IsTown,
                    __instance.IsNefia,
                    __instance.IsPCFaction,
                    __instance is Zone_Dungeon,
                    __instance.FeatureType == ZoneFeatureType.RandomField
                    );
        }

        [HarmonyPatch(typeof(CharaGen)), HarmonyPatch(nameof(CharaGen.Create)), HarmonyPostfix]
        public static void CharaGenCreatePostfix(Chara __result) {
            Debug.Log("CharaGen.Create");
            Dictionary<Hostility, List<string>> chara = new Dictionary<Hostility, List<string>>();
            SSTPClient client = SSTPClient.GetInstance();

            if (IsInZoneActivate) {
                return;
            }

            foreach (Hostility v in Enum.GetValues(typeof(Hostility))) {
                chara[v] = new List<string>();
            }

            Hostility h = __result.OriginalHostility;
            chara[h].Add(PackChara(__result));
            client.Enqueue("OnElinMapCharaGenerate",
                    string.Join("\x01", chara[Hostility.Enemy]),
                    string.Join("\x01", chara[Hostility.Neutral]),
                    string.Join("\x01", chara[Hostility.Friend]),
                    string.Join("\x01", chara[Hostility.Ally])
                    );
        }


        private static void EmitGenerate(Thing thing) {
            SSTPClient client = SSTPClient.GetInstance();
            string name = thing.GetName(NameStyle.Full);
            client.Enqueue("OnElinMapItemGenerate",
                    string.Join("\x01", PackThing(thing))
                    );
        }

        [HarmonyPatch(typeof(ThingGen)), HarmonyPatch(nameof(ThingGen.CreateTreasure)), HarmonyPostfix]
        public static void ThingGenCreateTreasurePostfix(Thing __result) {
            Debug.Log("ThingGen.CreateTreasure");
            EmitGenerate(__result);
        }

        [HarmonyPatch(typeof(ThingGen)), HarmonyPatch(nameof(ThingGen.CreateParcel)), HarmonyPostfix]
        public static void ThingGenCreateParcelPostfix(Thing __result) {
            Debug.Log("ThingGen.CreateParcel");
            EmitGenerate(__result);
        }
    }

    [HarmonyPatch(typeof(BaseCondition))]
    [HarmonyPatch(nameof(BaseCondition.Start))]
    class ConditionPatch {
        public static void Prefix() {
        }
        public static void Postfix(BaseCondition __instance) {
            SSTPClient client = SSTPClient.GetInstance();
            Chara c = __instance.Owner;
            if (c == null) {
                return;
            }
            if (c.IsPC) {
                client.Enqueue("OnElinPCCondition", c.NameBraced, __instance.Name, __instance.power);
            }
            else if (c.IsPCParty) {
                client.Enqueue("OnElinAllyCondition", c.NameBraced, __instance.Name, __instance.power);
            }
        }
    }

    [HarmonyPatch(typeof(Chara))]
    [HarmonyPatch(nameof(Chara.Die))]
    class CharaDiePatch {
        public static void Prefix() {
        }
        public static void Postfix(Chara __instance, Element __0, Card __1, AttackSource __2) {
            SSTPClient client = SSTPClient.GetInstance();
            string origin = "";
            if (__1 != null) {
                origin = __1.Name;
            }
            string reason = "";
            if (LangGame.Has("dead_" + __2)) {
                string[] source = {
                    "none",
                    "melee",
                    "range",
                    "hunger",
                    "fatigue",
                    "condition",
                    "weapon_enchant",
                    "burden",
                    "trap",
                    "fall",
                    "burden_stairs",
                    "burden_fall_down",
                    "throw",
                    "finish",
                    "hang",
                    "wrath"
                };
                reason = source[(int)__2];
            }
            else {
                if (__0 != Element.Void && __0 != null) {
                    reason = __0.source.alias;
                }
                if (!LangGame.Has("dead_" + reason)) {
                    reason = "none";
                }
            }
            if (__instance.IsPC) {
                client.Enqueue("OnElinPCDead", __instance.NameBraced, reason, origin);
            }
            else if (__instance.IsPCParty) {
                client.Enqueue("OnElinAllyDead", __instance.NameBraced, reason, origin);
            }
        }
    }

    [HarmonyPatch(typeof(Player))]
    [HarmonyPatch(nameof(Player.TargetRanged))]
    class PlayertargetSetterPatch {
        private static Chara target = null;

        public static void Prefix() {
        }
        public static void Postfix(Player __instance) {
            Debug.Log("Player.target");
            SSTPClient client = SSTPClient.GetInstance();
            if (target == __instance.target) {
                return;
            }
            target = __instance.target;
            if (target == null) {
                return;
            }
            client.Enqueue("OnElinTarget", target.Name);
        }
    }

    [HarmonyPatch(typeof(AI_Fish))]
    [HarmonyPatch(nameof(AI_Fish.Makefish))]
    class AI_FishMakefishPatch {
        public static void Prefix() {
        }
        public static void Postfix(Chara __0, Thing __result) {
            Debug.Log("AI_FishMakefish");
            if (!__0.IsPC) {
                return;
            }
            SSTPClient client = SSTPClient.GetInstance();
            client.Enqueue("OnElinCatchFish", __result.GetName(NameStyle.Full));
        }
    }
}
