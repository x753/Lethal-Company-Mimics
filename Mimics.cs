using BepInEx;
using DunGen;
using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Events;
using UnityEngine.Audio;
using BepInEx.Configuration;
using System.Linq;

namespace Mimics
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class Mimics : BaseUnityPlugin
    {
        private const string modGUID = "x753.Mimics";
        private const string modName = "Mimics";
        private const string modVersion = "2.4.0";

        private readonly Harmony harmony = new Harmony(modGUID);

        private static Mimics Instance;

        public static GameObject MimicPrefab;
        public static GameObject MimicNetworkerPrefab;
        public static TerminalNode MimicFile;

        public static int MimicCreatureID = 0;

        public static int[] SpawnRates;
        public static bool MimicPerfection;
        public static bool EasyMode;
        public static bool ColorBlindMode;
        public static float MimicVolume;
        public static bool DynamicSpawnRate;

        public static List<string> InteriorWhitelist;

        private void Awake()
        {
            AssetBundle mimicAssetBundle = AssetBundle.LoadFromMemory(LethalCompanyMimics.Properties.Resources.mimicdoor);
            MimicPrefab = mimicAssetBundle.LoadAsset<GameObject>("Assets/MimicDoor.prefab");
            MimicNetworkerPrefab = mimicAssetBundle.LoadAsset<GameObject>("Assets/MimicNetworker.prefab");
            MimicFile = mimicAssetBundle.LoadAsset<TerminalNode>("Assets/MimicFile.asset");

            if (Instance == null)
            {
                Instance = this;
            }

            harmony.PatchAll();
            Logger.LogInfo($"Plugin {modName} is loaded!");

            // Handle configs
            {
                string interiorWhitelistString = Config.Bind("Compatibility", "Whitelisted Interiors", "Level1Flow, Level1FlowExtraLarge, Level2Flow, OfficeDungeonFlow", "Comma separated list of interiors that mimics can spawn in. Not all interiors will work.").Value;
                InteriorWhitelist = interiorWhitelistString.ToLower().Split(',').Select(s => s.Trim()).ToList();

                SpawnRates = new int[] {
                    Config.Bind("Spawn Rate", "Zero Mimics", 23, "Weight of zero mimics spawning").Value,
                    Config.Bind("Spawn Rate", "One Mimic", 69, "Weight of one mimic spawning").Value,
                    Config.Bind("Spawn Rate", "Two Mimics", 7, "Weight of two mimics spawning").Value,
                    Config.Bind("Spawn Rate", "Three Mimics", 1, "Weight of three mimics spawning").Value,
                    Config.Bind("Spawn Rate", "Four Mimics", 0, "Weight of four mimics spawning").Value,
                    Config.Bind("Spawn Rate", "Maximum Mimics", 0, "Weight of maximum mimics spawning").Value
                };

                DynamicSpawnRate = Config.Bind("Spawn Rate", "Dynamic Spawn Rate", true, "Increases mimic spawn rate based on dungeon size and the number of instances of the real thing.").Value;

                MimicPerfection = Config.Bind("Difficulty", "Perfect Mimics", false, "Select this if you want mimics to be the exact same color as the real thing. Overrides all difficulty settings.").Value;

                EasyMode = Config.Bind("Difficulty", "Easy Mode", false, "Each mimic will have one of several possible imperfections to help you tell if it's a mimic.").Value;

                ColorBlindMode = Config.Bind("Difficulty", "Color Blind Mode", false, "Replaces all color differences with another way to differentiate mimics.").Value;

                MimicVolume = Config.Bind("General", "SFX Volume", 100, "Volume of the mimic's SFX (0-100)").Value;
                if (MimicVolume < 0) { MimicVolume = 0; }
                if (MimicVolume > 100) { MimicVolume = 100; }

                // This is necessary to delete old config entries that might confuse people
                Config.Bind("Difficulty", "Difficulty Level", 0, "This is an old setting, ignore it.");
                Config.Remove(Config["Difficulty", "Difficulty Level"].Definition);
                Config.Bind("Spawn Rate", "Five Mimics", 0, "This is an old setting, ignore it.");
                Config.Remove(Config["Spawn Rate", "Five Mimics"].Definition);
                Config.Save();
            }

            // UnityNetcodeWeaver patch requires this
            var types = Assembly.GetExecutingAssembly().GetTypes();
            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var method in methods)
                {
                    var attributes = method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false);
                    if (attributes.Length > 0)
                    {
                        method.Invoke(null, null);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager))]
        internal class GameNetworkManagerPatch
        {
            [HarmonyPatch("Start")]
            [HarmonyPostfix]
            static void StartPatch()
            {
                GameNetworkManager.Instance.GetComponent<NetworkManager>().AddNetworkPrefab(MimicNetworkerPrefab); // Register the networker prefab
            }
        }

        [HarmonyPatch(typeof(StartOfRound))]
        internal class StartOfRoundPatch
        {
            [HarmonyPatch("Start")]
            [HarmonyPostfix]
            static void StartPatch(ref StartOfRound __instance)
            {

                // Handle networking with a single game object
                if (__instance.IsServer && MimicNetworker.Instance == null)
                {
                    GameObject mimicNetworker = Instantiate<GameObject>(MimicNetworkerPrefab);
                    mimicNetworker.GetComponent<NetworkObject>().Spawn(true);

                    MimicNetworker.SpawnWeight0.Value = SpawnRates[0];
                    MimicNetworker.SpawnWeight1.Value = SpawnRates[1];
                    MimicNetworker.SpawnWeight2.Value = SpawnRates[2];
                    MimicNetworker.SpawnWeight3.Value = SpawnRates[3];
                    MimicNetworker.SpawnWeight4.Value = SpawnRates[4];
                    MimicNetworker.SpawnWeightMax.Value = SpawnRates[5];
                    MimicNetworker.SpawnRateDynamic.Value = DynamicSpawnRate;
                }
            }
        }

        [HarmonyPatch(typeof(Terminal))]
        internal class TerminalPatch
        {
            [HarmonyPatch("Start")]
            [HarmonyPostfix]
            static void StartPatch(ref StartOfRound __instance)
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                if (!terminal.enemyFiles.Find(node => node.creatureName == "Mimics"))
                {
                    // Add mimics to the bestiary
                    MimicCreatureID = terminal.enemyFiles.Count;
                    MimicFile.creatureFileID = MimicCreatureID;
                    terminal.enemyFiles.Add(MimicFile);

                    TerminalKeyword infoKeyword = terminal.terminalNodes.allKeywords.First(keyword => keyword.word == "info");

                    TerminalKeyword mimicKeyword = new TerminalKeyword()
                    {
                        word = "mimics",
                        isVerb = false,
                        defaultVerb = infoKeyword
                    };

                    List<CompatibleNoun> itemInfoNouns = infoKeyword.compatibleNouns.ToList();
                    itemInfoNouns.Add(new CompatibleNoun()
                    {
                        noun = mimicKeyword,
                        result = MimicFile
                    });
                    infoKeyword.compatibleNouns = itemInfoNouns.ToArray();

                    List<TerminalKeyword> allKeywords = terminal.terminalNodes.allKeywords.ToList();
                    allKeywords.Add(mimicKeyword);
                    terminal.terminalNodes.allKeywords = allKeywords.ToArray();
                }
            }
        }

        [HarmonyPatch(typeof(RoundManager))]
        internal class RoundManagerPatch
        {
            [HarmonyPatch("SetExitIDs")]
            [HarmonyPostfix]
            static void SetExitIDsPatch(ref RoundManager __instance, Vector3 mainEntrancePosition)
            {
                if (MimicNetworker.Instance == null) { return; } // if the host doesn't have the mod, don't spawn mimics!

                MimicDoor.allMimics = new List<MimicDoor>();
                int mIndex = 0;

                Dungeon dungeon = __instance.dungeonGenerator.Generator.CurrentDungeon;

                if (!InteriorWhitelist.Contains(dungeon.DungeonFlow.name.ToLower().Trim())) { return; } // do not spawn mimics for dungeon flows that aren't whitelisted

                // Spawn a number of mimics based on the spawn weights
                int numMimics = 0;
                {
                    int[] spawnRates = new int[] { MimicNetworker.SpawnWeight0.Value, MimicNetworker.SpawnWeight1.Value, MimicNetworker.SpawnWeight2.Value, MimicNetworker.SpawnWeight3.Value, MimicNetworker.SpawnWeight4.Value, MimicNetworker.SpawnWeightMax.Value };

                    int totalWeight = 0;
                    foreach (int rate in spawnRates)
                    {
                        totalWeight += rate;
                    }
                    System.Random random = new System.Random(StartOfRound.Instance.randomMapSeed + 753);
                    int randomSpawnRate = random.Next(0, totalWeight);

                    int accumulatedWeight = 0;
                    for (int i = 0; i < spawnRates.Length; i++)
                    {
                        if (randomSpawnRate < spawnRates[i] + accumulatedWeight)
                        {
                            numMimics = i;
                            break;
                        }
                        accumulatedWeight += spawnRates[i];
                    }

                    if (numMimics == 5) // if the Max Mimics spawn rate rolled, just make numMimics absurdly large
                    {
                        numMimics = 999;
                    }

                    EntranceTeleport[] entranceTeleports = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>(false);
                    int fireExitCount = (entranceTeleports.Length - 2) / 2;
                    if (MimicNetworker.SpawnRateDynamic.Value && numMimics < fireExitCount && fireExitCount > 1)
                    {
                        numMimics += random.Next(0, 2); // 50% chance to add another mimic if there isn't at least one for every fire exit and there's more than 1 fire exit on the moon
                    }
                    if (MimicNetworker.SpawnRateDynamic.Value && dungeon.AllTiles.Count > 100)
                    {
                        numMimics += random.Next(0, 2); // 50% chance to add another mimic if the dungeon is large
                    }
                }

                List<Doorway> validDoors = new List<Doorway>();
                foreach (Tile tile in dungeon.AllTiles)
                {
                    foreach (Doorway doorway in tile.UnusedDoorways)
                    {
                        if (doorway.HasDoorPrefabInstance) { continue; } // can't spawn a mimic if there's already a door there

                        if (doorway.GetComponentInChildren<SpawnSyncedObject>(true) == null) { continue; }
                        GameObject alleyExitDoorContainer = doorway.GetComponentInChildren<SpawnSyncedObject>(true).transform.parent.gameObject;

                        if (!alleyExitDoorContainer.name.StartsWith("AlleyExitDoorContainer")) { continue; } // only put mimics where valid fire exits can be

                        if (alleyExitDoorContainer.activeSelf) { continue; } // skip the real fire exit

                        bool badPosition = false;
                        Matrix4x4 rotationMatrix = Matrix4x4.TRS(doorway.transform.position, doorway.transform.rotation, new Vector3(1f, 1f, 1f));
                        Bounds bounds = new Bounds(new Vector3(0f, 1.5f, 5.5f), new Vector3(2f, 6f, 8f));
                        bounds.center = rotationMatrix.MultiplyPoint3x4(bounds.center);
                        Collider[] badPositionCheck = Physics.OverlapBox(bounds.center, bounds.extents, doorway.transform.rotation, LayerMask.GetMask("Room", "Railing", "MapHazards")); 
                        foreach (Collider collider in badPositionCheck)
                        {
                            badPosition = true;
                            break;
                        }
                        if (badPosition) { continue; }

                        foreach (Tile otherTile in dungeon.AllTiles)
                        {
                            if (tile == otherTile) { continue; }
                            Vector3 mimicLocation = doorway.transform.position + 5 * doorway.transform.forward;

                            Bounds tileBounds = UnityUtil.CalculateProxyBounds(otherTile.gameObject, true, Vector3.up);

                            if (tileBounds.IntersectRay(new Ray() { origin = mimicLocation, direction = Vector3.up}))
                            {
                                if (otherTile.name.Contains("Catwalk") || otherTile.name.Contains("LargeForkTile") || otherTile.name.Contains("4x4BigStair") || otherTile.name.Contains("ElevatorConnector") || (otherTile.name.Contains("StartRoom") && !otherTile.name.Contains("Manor")))
                                {
                                    badPosition = true; // LargeForkTileB has way bigger bounds than it should which kills some mimics it shouldn't
                                }
                            }
                            if (tileBounds.IntersectRay(new Ray() { origin = mimicLocation, direction = Vector3.down }))
                            {
                                if (otherTile.name.Contains("MediumRoomHallway1B") || otherTile.name.Contains("LargeForkTile") || otherTile.name.Contains("4x4BigStair") || otherTile.name.Contains("ElevatorConnector") || otherTile.name.Contains("StartRoom"))
                                {
                                    badPosition = true; // LargeForkTileB has way bigger bounds than it should which kills some mimics it shouldn't
                                }
                            }
                        }
                        if (badPosition) { continue; }

                        validDoors.Add(doorway);
                    }
                }

                Shuffle<Doorway>(validDoors, StartOfRound.Instance.randomMapSeed);

                List<Vector3> mimicLocations = new List<Vector3>();
                foreach (Doorway doorway in validDoors)
                {
                    if (mIndex >= numMimics)
                    {
                        return;
                    }

                    bool locationAlreadyUsed = false;
                    Vector3 newLocation = doorway.transform.position + 5*doorway.transform.forward;
                    foreach (Vector3 mimicLocation in mimicLocations)
                    {
                        if (Vector3.Distance(newLocation, mimicLocation) < 4f)
                        {
                            locationAlreadyUsed = true;
                            break;
                        }
                    }
                    if (locationAlreadyUsed) { continue; }

                    mimicLocations.Add(newLocation);

                    GameObject alleyExitDoorContainer = doorway.GetComponentInChildren<SpawnSyncedObject>(true).transform.parent.gameObject;

                    GameObject mimic = Instantiate<GameObject>(MimicPrefab, doorway.transform);
                    mimic.transform.position = alleyExitDoorContainer.transform.position;
                    MimicDoor mimicDoor = mimic.GetComponent<MimicDoor>();

                    mimicDoor.scanNode.creatureScanID = MimicCreatureID;

                    foreach (AudioSource audio in mimic.GetComponentsInChildren<AudioSource>(true))
                    {
                        audio.volume = MimicVolume / 100f;
                        audio.outputAudioMixerGroup = StartOfRound.Instance.ship3DAudio.outputAudioMixerGroup;
                    }

                    if (SpawnRates[5] == 9753)
                    {
                        // Sometimes we need to spawn a mimic near the ship for testing
                        if (mIndex == 0) { mimic.transform.position = new Vector3(-7f, 0f, -10f); }
                    }

                    // We can handle networking by just indexing the mimics
                    MimicDoor.allMimics.Add(mimicDoor);
                    mimicDoor.mimicIndex = mIndex;
                    mIndex++;

                    GameObject wall = doorway.transform.GetChild(0).gameObject;
                    wall.SetActive(false); // turn off the wall behind the mimic

                    foreach (Collider collider in Physics.OverlapBox(mimicDoor.frameBox.bounds.center, mimicDoor.frameBox.bounds.extents, Quaternion.identity))
                    {
                        if (collider.gameObject.name.Contains("Shelf"))
                        {
                            collider.gameObject.SetActive(false);
                        }
                    }

                    Light light = alleyExitDoorContainer.GetComponentInChildren<Light>(true);
                    light.transform.parent.SetParent(mimic.transform);

                    MeshRenderer[] meshes = mimic.GetComponentsInChildren<MeshRenderer>();
                    foreach (MeshRenderer mesh in meshes)
                    {
                        foreach (Material mat in mesh.materials)
                        {
                            mat.shader = wall.GetComponentInChildren<MeshRenderer>(true).material.shader; // give the mimic the wall's shader which is properly set up
                            mat.renderQueue = wall.GetComponentInChildren<MeshRenderer>(true).material.renderQueue;
                        }
                    }

                    mimicDoor.interactTrigger.onInteract = new InteractEvent();
                    mimicDoor.interactTrigger.onInteract.AddListener(mimicDoor.TouchMimic);

                    // Handle all difficulty options here:
                    if (!MimicPerfection)
                    {
                        mimicDoor.interactTrigger.timeToHold = 0.9f; // original: 0.8f;

                        if (!ColorBlindMode)
                        {
                            // By default we'll just ever so slightly change the color
                            if ((StartOfRound.Instance.randomMapSeed + mIndex) % 2 == 0)
                            {
                                mimic.GetComponentsInChildren<MeshRenderer>()[0].material.color = new Color(0.490566f, 0.1226415f, 0.1302275f); // original: new Color(0.489f, 0.1415526f, 0.1479868f);
                                mimic.GetComponentsInChildren<MeshRenderer>()[1].material.color = new Color(0.4339623f, 0.1043965f, 0.1150277f); // original: new Color(0.427451f, 0.1254902f, 0.1294117f);
                                light.colorTemperature = 1250;
                            }
                            else
                            {
                                mimic.GetComponentsInChildren<MeshRenderer>()[0].material.color = new Color(0.5f, 0.1580188f, 0.1657038f); // original: new Color(0.489f, 0.1415526f, 0.1479868f);
                                mimic.GetComponentsInChildren<MeshRenderer>()[1].material.color = new Color(0.4056604f, 0.1358579f, 0.1393619f); // original: new Color(0.427451f, 0.1254902f, 0.1294117f);
                                light.colorTemperature = 1300;
                            }
                        }
                        else
                        {
                            // For color blind people change the interaction time instead
                            if ((StartOfRound.Instance.randomMapSeed + mIndex) % 2 == 0)
                            {
                                mimicDoor.interactTrigger.timeToHold = 1.1f; // original: 0.8f;
                            }
                            else
                            {
                                mimicDoor.interactTrigger.timeToHold = 1f; // original: 0.8f;
                            }
                        }

                        if (EasyMode)
                        {
                            // Randomly assign an imperfection to the mimic
                            System.Random random = new System.Random(StartOfRound.Instance.randomMapSeed + mIndex);
                            int imperfectionIndex = random.Next(0, 4);

                            switch (imperfectionIndex)
                            {
                                case 0:
                                    if (!ColorBlindMode)
                                    {
                                        mimic.GetComponentsInChildren<MeshRenderer>()[0].material.color = new Color(0.489f, 0.2415526f, 0.1479868f);
                                        mimic.GetComponentsInChildren<MeshRenderer>()[1].material.color = new Color(0.489f, 0.2415526f, 0.1479868f);
                                    }
                                    else
                                    {
                                        mimicDoor.interactTrigger.timeToHold = 1.5f;
                                    }
                                    break;

                                case 1:
                                    mimicDoor.interactTrigger.hoverTip = "Feed : [LMB]";
                                    mimicDoor.interactTrigger.holdTip = "Feed : [LMB]";
                                    break;

                                case 2:
                                    mimicDoor.interactTrigger.hoverIcon = mimicDoor.LostFingersIcon;
                                    break;

                                case 3:
                                    mimicDoor.interactTrigger.holdTip = "DIE : [LMB]";
                                    mimicDoor.interactTrigger.timeToHold = 0.5f;
                                    break;

                                default:
                                    mimicDoor.interactTrigger.hoverTip = "BUG, REPORT TO DEVELOPER";
                                    break;
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(SprayPaintItem))]
        internal class SprayPaintItemPatch
        {
            static FieldInfo SprayHit = typeof(SprayPaintItem).GetField("sprayHit", BindingFlags.NonPublic | BindingFlags.Instance);

            [HarmonyPatch("SprayPaintClientRpc")]
            [HarmonyPostfix]
            static void SprayPaintClientRpcPatch(SprayPaintItem __instance, Vector3 sprayPos, Vector3 sprayRot)
            {
                if (MimicNetworker.Instance == null) { return; }

                RaycastHit raycastHit = (RaycastHit) SprayHit.GetValue(__instance);

                if (raycastHit.collider != null && raycastHit.collider.name == "MimicSprayCollider")
                {
                    MimicDoor mimic = raycastHit.collider.transform.parent.parent.GetComponent<MimicDoor>();
                    mimic.sprayCount += 1;

                    if (mimic.sprayCount > 8)
                    {
                        MimicNetworker.Instance.MimicAddAnger(1, mimic.mimicIndex);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(LockPicker))]
        internal class LockPickerPatch
        {
            static FieldInfo RayHit = typeof(LockPicker).GetField("hit", BindingFlags.NonPublic | BindingFlags.Instance);

            [HarmonyPatch("ItemActivate")]
            [HarmonyPostfix]
            static void ItemActivatePatch(LockPicker __instance, bool used, bool buttonDown = true)
            {
                if (MimicNetworker.Instance == null) { return; }

                RaycastHit raycastHit = (RaycastHit)RayHit.GetValue(__instance);

                if (__instance.playerHeldBy == null) { return; }
                if (raycastHit.Equals(default(RaycastHit))) { return; }

                if (raycastHit.transform.parent == null) { return; }
                Transform mimic = raycastHit.transform.parent;
                if (mimic.name.StartsWith("MimicDoor"))
                {
                    LockPicker lockPicker = __instance;
                    MimicNetworker.Instance.MimicLockPick(lockPicker, mimic.GetComponent<MimicDoor>().mimicIndex);
                }
            }
        }

        public static void Shuffle<T>(IList<T> list, int seed)
        {
            var rng = new System.Random(seed);
            int n = list.Count;

            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
    }

    public class MimicNetworker : NetworkBehaviour
    {
        public static MimicNetworker Instance;

        public static NetworkVariable<int> SpawnWeight0 = new NetworkVariable<int>();
        public static NetworkVariable<int> SpawnWeight1 = new NetworkVariable<int>();
        public static NetworkVariable<int> SpawnWeight2 = new NetworkVariable<int>();
        public static NetworkVariable<int> SpawnWeight3 = new NetworkVariable<int>();
        public static NetworkVariable<int> SpawnWeight4 = new NetworkVariable<int>();
        public static NetworkVariable<int> SpawnWeightMax = new NetworkVariable<int>();
        public static NetworkVariable<bool> SpawnRateDynamic = new NetworkVariable<bool>();

        private void Awake()
        {
            Instance = this;
        }

        public void MimicAttack(int playerId, int mimicIndex, bool ownerOnly = false)
        {
            if (base.IsOwner)
            {
                MimicNetworker.Instance.MimicAttackClientRpc(playerId, mimicIndex);
            }
            else if(!ownerOnly)
            {
                MimicNetworker.Instance.MimicAttackServerRpc(playerId, mimicIndex);
            }
        }

        [ClientRpc]
        public void MimicAttackClientRpc(int playerId, int mimicIndex)
        {
            StartCoroutine(MimicDoor.allMimics[mimicIndex].Attack(playerId));
        }

        [ServerRpc(RequireOwnership = false)]
        public void MimicAttackServerRpc(int playerId, int mimicIndex)
        {
            MimicNetworker.Instance.MimicAttackClientRpc(playerId, mimicIndex);
        }

        public void MimicAddAnger(int amount, int mimicIndex)
        {
            if (base.IsOwner)
            {
                MimicNetworker.Instance.MimicAddAngerClientRpc(amount, mimicIndex);
            }
            else
            {
                MimicNetworker.Instance.MimicAddAngerServerRpc(amount, mimicIndex);
            }
        }

        [ClientRpc]
        public void MimicAddAngerClientRpc(int amount, int mimicIndex)
        {
            StartCoroutine(MimicDoor.allMimics[mimicIndex].AddAnger(amount));
        }

        [ServerRpc(RequireOwnership = false)]
        public void MimicAddAngerServerRpc(int amount, int mimicIndex)
        {
            MimicNetworker.Instance.MimicAddAngerClientRpc(amount, mimicIndex);
        }

        public void MimicLockPick(LockPicker lockPicker, int mimicIndex, bool ownerOnly = false)
        {
            int playerId = (int) lockPicker.playerHeldBy.playerClientId;
            if (base.IsOwner)
            {
                MimicNetworker.Instance.MimicLockPickClientRpc(playerId, mimicIndex);
            }
            else if (!ownerOnly)
            {
                MimicNetworker.Instance.MimicLockPickServerRpc(playerId, mimicIndex);
            }
        }

        [ClientRpc]
        public void MimicLockPickClientRpc(int playerId, int mimicIndex)
        {
            StartCoroutine(MimicDoor.allMimics[mimicIndex].MimicLockPick(playerId));
        }

        [ServerRpc(RequireOwnership = false)]
        public void MimicLockPickServerRpc(int playerId, int mimicIndex)
        {
            MimicNetworker.Instance.MimicLockPickClientRpc(playerId, mimicIndex);
        }
    }

    public class MimicDoor : MonoBehaviour
    {
        public GameObject playerTarget;

        public BoxCollider frameBox;

        public Sprite LostFingersIcon;

        public Animator mimicAnimator;

        public GameObject grabPoint;

        public InteractTrigger interactTrigger;

        public ScanNodeProperties scanNode;

        public int anger = 0;

        public bool angering = false;

        public int sprayCount = 0;

        private bool attacking = false;

        public static List<MimicDoor> allMimics;
        public int mimicIndex;

        public void TouchMimic(PlayerControllerB player)
        {
            if (!attacking)
            {
                MimicNetworker.Instance.MimicAttack((int)player.playerClientId, mimicIndex);
            }
        }

        public IEnumerator Attack(int playerId)
        {
            attacking = true;
            interactTrigger.interactable = false;

            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerId];

            mimicAnimator.SetTrigger("Attack");

            playerTarget.transform.position = player.transform.position;
            yield return new WaitForSeconds(0.1f);
            playerTarget.transform.position = player.transform.position;
            yield return new WaitForSeconds(0.1f);
            playerTarget.transform.position = player.transform.position;
            yield return new WaitForSeconds(0.1f);
            playerTarget.transform.position = player.transform.position;
            yield return new WaitForSeconds(0.1f);

            float proximity = Vector3.Distance(GameNetworkManager.Instance.localPlayerController.transform.position, frameBox.transform.position);
            if (proximity < 8f)
            {
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            }
            else if (proximity < 14f)
            {
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Small);
            }

            yield return new WaitForSeconds(0.2f);

            if (player.IsOwner && Vector3.Distance(player.transform.position, this.transform.position) < 8.45f)
            {
                player.KillPlayer(Vector3.zero, true, CauseOfDeath.Unknown, 0);
            }

            float startTime = Time.timeSinceLevelLoad;
            yield return new WaitUntil(() => player.deadBody != null || Time.timeSinceLevelLoad - startTime > 4f);

            if (player.deadBody != null)
            {
                player.deadBody.attachedTo = grabPoint.transform;
                player.deadBody.attachedLimb = player.deadBody.bodyParts[5];
                player.deadBody.matchPositionExactly = true;

                for (int i = 0; i < player.deadBody.bodyParts.Length; i++)
                {
                    player.deadBody.bodyParts[i].GetComponent<Collider>().excludeLayers = ~0;
                }
            }

            yield return new WaitForSeconds(2f);

            if (player.deadBody != null)
            {
                player.deadBody.attachedTo = null;
                player.deadBody.attachedLimb = null;
                player.deadBody.matchPositionExactly = false;
                player.deadBody.transform.GetChild(0).gameObject.SetActive(false); // don't set the dead body itself inactive or it won't get cleaned up later
                player.deadBody = null;
            }

            yield return new WaitForSeconds(4.5f);
            attacking = false;
            interactTrigger.interactable = true;
            yield break;
        }

        static MethodInfo RetractClaws = typeof(LockPicker).GetMethod("RetractClaws", BindingFlags.NonPublic | BindingFlags.Instance);
        public IEnumerator MimicLockPick(int playerId)
        {
            if (angering) { yield break; }
            if (attacking) { yield break; }

            LockPicker lockPicker = StartOfRound.Instance.allPlayerScripts[playerId].currentlyHeldObjectServer as LockPicker;
            if (lockPicker == null) { yield break; }

            attacking = true;
            interactTrigger.interactable = false;

            AudioSource lockPickerAudio = lockPicker.GetComponent<AudioSource>();
            lockPickerAudio.PlayOneShot(lockPicker.placeLockPickerClips[UnityEngine.Random.Range(0, lockPicker.placeLockPickerClips.Length)]);
            lockPicker.armsAnimator.SetBool("mounted", true);
            lockPicker.armsAnimator.SetBool("picking", true);
            lockPickerAudio.Play();
            lockPickerAudio.pitch = UnityEngine.Random.Range(0.94f, 1.06f);
            lockPicker.isOnDoor = true;
            lockPicker.isPickingLock = true;
            lockPicker.grabbable = false;

            if (lockPicker.IsOwner)
            {
                lockPicker.playerHeldBy.DiscardHeldObject(true, MimicNetworker.Instance.NetworkObject, this.transform.position + this.transform.up * 1.5f - this.transform.forward * 1.15f, true);
            }

            float startTime = Time.timeSinceLevelLoad;
            yield return new WaitUntil(() => !lockPicker.isHeld || Time.timeSinceLevelLoad - startTime > 10f);
            lockPicker.transform.localEulerAngles = new Vector3(this.transform.eulerAngles.x, this.transform.eulerAngles.y + 90f, this.transform.eulerAngles.z);

            yield return new WaitForSeconds(5f); // wait 5 seconds before the lockpicker falls off

            RetractClaws.Invoke(lockPicker, null);
            lockPicker.transform.SetParent(null);
            lockPicker.startFallingPosition = lockPicker.transform.position;
            lockPicker.FallToGround();
            lockPicker.grabbable = true;

            yield return new WaitForSeconds(1f);

            anger = 3;
            attacking = false;
            interactTrigger.interactable = false;
            PlayerControllerB closestPlayer = null;
            float closestDistance = 9999f;
            foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
            {
                float distance = Vector3.Distance(this.transform.position, player.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestPlayer = player;
                }
            }
            if (closestPlayer != null)
            {
                MimicNetworker.Instance.MimicAttackClientRpc((int)closestPlayer.playerClientId, this.mimicIndex);
            }
            else
            {
                interactTrigger.interactable = true;
            }

            yield break;
        }

        public IEnumerator AddAnger(int amount)
        {
            if (angering) { yield break; }
            if (attacking) { yield break; }

            angering = true;
            anger += amount;

            if (anger == 1)
            {
                Sprite oldIcon = interactTrigger.hoverIcon;
                interactTrigger.hoverIcon = LostFingersIcon;
                mimicAnimator.SetTrigger("Growl");
                yield return new WaitForSeconds(2.75f);
                interactTrigger.hoverIcon = oldIcon;

                sprayCount = 0;
                angering = false;
                yield break;
            }
            else if (anger == 2)
            {
                interactTrigger.holdTip = "DIE : [LMB]";
                interactTrigger.timeToHold = 0.25f;

                Sprite oldIcon = interactTrigger.hoverIcon;
                interactTrigger.hoverIcon = LostFingersIcon;
                mimicAnimator.SetTrigger("Growl");
                yield return new WaitForSeconds(2.75f);
                interactTrigger.hoverIcon = oldIcon;

                sprayCount = 0;
                angering = false;
                yield break;
            }
            else if (anger > 2)
            {
                PlayerControllerB closestPlayer = null;
                float closestDistance = 9999f;
                foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
                {
                    float distance = Vector3.Distance(this.transform.position, player.transform.position);
                    if (distance < closestDistance)
                    { 
                        closestDistance = distance;
                        closestPlayer = player;
                    }
                }
                if (closestPlayer != null)
                {
                    MimicNetworker.Instance.MimicAttackClientRpc((int) closestPlayer.playerClientId, this.mimicIndex);
                }
            }

            sprayCount = 0;
            angering = false;
            yield break;
        }
    }

    public class MimicCollider : MonoBehaviour, IHittable
    {
        public MimicDoor mimic;

        bool IHittable.Hit(int force, Vector3 hitDirection, PlayerControllerB playerWhoHit = null, bool playHitSFX = false)
        {
            MimicNetworker.Instance.MimicAddAnger(force, mimic.mimicIndex);
            return true;
        }
    }

    public class MimicListener : MonoBehaviour, INoiseListener
    {
        public MimicDoor mimic;

        private int tolerance = 100;

        void INoiseListener.DetectNoise(Vector3 noisePosition, float noiseLoudness, int timesPlayedInOneSpot, int noiseID)
        {
            if ((noiseLoudness >= 0.9f || noiseID == 101158) && Vector3.Distance(noisePosition, mimic.transform.position) < 5f)
            {
                if (noiseID == 75) // player voice
                {
                    tolerance -= 1;
                }
                else if (noiseID == 5) // boombox
                {
                    tolerance -= 15;
                }
                else if (noiseID == 101158) // whoopie cushion
                {
                    tolerance -= 35;
                }
                else
                {
                    tolerance -= 30; // others such as the clown horn
                }

                if (tolerance <= 0)
                {
                    tolerance = 100;
                    MimicNetworker.Instance.MimicAddAnger(1, mimic.mimicIndex);
                }
            }
        }
    }
}