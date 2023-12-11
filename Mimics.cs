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

namespace Mimics
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class Mimics : BaseUnityPlugin
    {
        private const string modGUID = "x753.Mimics";
        private const string modName = "Mimics";
        private const string modVersion = "2.0.0";

        private readonly Harmony harmony = new Harmony(modGUID);

        private static Mimics Instance;

        public static GameObject MimicPrefab;
        public static GameObject MimicNetworkerPrefab;

        public static int[] SpawnRates;
        public static bool MimicPerfection;
        public static float MimicVolume;

        private void Awake()
        {
            AssetBundle mimicAssetBundle = AssetBundle.LoadFromMemory(LethalCompanyMimics.Properties.Resources.mimicdoor);
            MimicPrefab = mimicAssetBundle.LoadAsset<GameObject>("Assets/MimicDoor.prefab");
            MimicNetworkerPrefab = mimicAssetBundle.LoadAsset<GameObject>("Assets/MimicNetworker.prefab");

            if (Instance == null)
            {
                Instance = this;
            }

            harmony.PatchAll();
            Logger.LogInfo($"Plugin {modName} is loaded!");

            // Handle configs
            {
                SpawnRates = new int[] {
                    Config.Bind("Spawn Rate", "Zero Mimics", 20, "Weight of zero mimics spawning").Value,
                    Config.Bind("Spawn Rate", "One Mimic", 70, "Weight of one mimic spawning").Value,
                    Config.Bind("Spawn Rate", "Two Mimics", 8, "Weight of two mimics spawning").Value,
                    Config.Bind("Spawn Rate", "Three Mimics", 1, "Weight of three mimics spawning").Value,
                    Config.Bind("Spawn Rate", "Four Mimics", 1, "Weight of four mimics spawning").Value,
                    Config.Bind("Spawn Rate", "Five Mimics", 0, "Weight of five mimics spawning").Value,
                    Config.Bind("Spawn Rate", "Maximum Mimics", 0, "Weight of maximum mimics spawning").Value
                };

                MimicPerfection = Config.Bind("Difficulty", "Perfect Mimics", false, "Select this if you want mimics to be the exact same color as the real thing.").Value;

                MimicVolume = Config.Bind("General", "SFX Volume", 100, "Volume of the mimic's SFX (0-100)").Value;
                if (MimicVolume < 0) { MimicVolume = 0; }
                if (MimicVolume > 100) { MimicVolume = 100; }
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

        [HarmonyPatch(typeof(RoundManager))]
        internal class RoundManagerPatch
        {
            [HarmonyPatch("SetExitIDs")]
            [HarmonyPostfix]
            static void SetExitIDsPatch(ref RoundManager __instance, Vector3 mainEntrancePosition)
            {
                // Handle networking with a single game object
                if (__instance.IsServer && MimicNetworker.Instance == null)
                {
                    GameObject mimicNetworker = Instantiate<GameObject>(MimicNetworkerPrefab);
                    mimicNetworker.GetComponent<NetworkObject>().Spawn(true);
                }
                MimicDoor.allMimics = new List<MimicDoor>();
                int mIndex = 0;

                Dungeon dungeon = __instance.dungeonGenerator.Generator.CurrentDungeon;

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
                    // Spawn a number of mimics based on the spawn weights
                    {
                        int numMimics = 0;

                        int totalWeight = 0;
                        foreach (int rate in SpawnRates)
                        {
                            totalWeight += rate;
                        }
                        System.Random random = new System.Random(StartOfRound.Instance.randomMapSeed + 753);
                        int randomSpawnRate = random.Next(0, totalWeight);

                        int accumulatedWeight = 0;
                        for (int i = 0; i < SpawnRates.Length; i++)
                        {
                            if (randomSpawnRate < SpawnRates[i] + accumulatedWeight)
                            {
                                numMimics = i;
                                break;
                            }
                            accumulatedWeight += SpawnRates[i];
                        }

                        if (numMimics == mIndex && numMimics != 6)
                        {
                            return;
                        }
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

                    foreach (AudioSource audio in mimic.GetComponentsInChildren<AudioSource>(true))
                    {
                        audio.volume = MimicVolume / 100f;
                        audio.outputAudioMixerGroup = StartOfRound.Instance.ship3DAudio.outputAudioMixerGroup;
                    }

                    // Sometimes we need to spawn a mimic near spawn for testing
                    //if (mIndex == 0) { mimic.transform.position = new Vector3(-7f, 0f, -10f); }

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

                    if (!MimicPerfection) // Slightly change the mimic's color unless the config wants perfectly identical mimics
                    {
                        if ((StartOfRound.Instance.randomMapSeed + mIndex) % 2 == 0)
                        {
                            mimic.GetComponentsInChildren<MeshRenderer>()[0].material.color = new Color(0.490566f, 0.1226415f, 0.1302275f); // original: new Color(0.489f, 0.1415526f, 0.1479868f);
                            mimic.GetComponentsInChildren<MeshRenderer>()[1].material.color = new Color(0.4339623f, 0.1043965f, 0.1150277f); // original: new Color(0.427451f, 0.1254902f, 0.1294117f);
                            light.colorTemperature = 1050;
                        }
                        else
                        {
                            mimic.GetComponentsInChildren<MeshRenderer>()[0].material.color = new Color(0.5f, 0.1580188f, 0.1657038f); // original: new Color(0.489f, 0.1415526f, 0.1479868f);
                            mimic.GetComponentsInChildren<MeshRenderer>()[1].material.color = new Color(0.4056604f, 0.1358579f, 0.1393619f); // original: new Color(0.427451f, 0.1254902f, 0.1294117f);
                            light.colorTemperature = 1150;
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
                RaycastHit raycastHit = (RaycastHit) SprayHit.GetValue(__instance);

                if (raycastHit.collider.name == "MimicSprayCollider")
                {
                    MimicDoor mimic = raycastHit.collider.transform.parent.parent.GetComponent<MimicDoor>();
                    mimic.sprayCount += 1;

                    if (mimic.sprayCount > 9)
                    {
                        MimicNetworker.Instance.MimicAddAnger(1, mimic.mimicIndex);
                    }
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
    }

    public class MimicDoor : MonoBehaviour
    {
        public GameObject playerTarget;

        public BoxCollider frameBox;

        public Sprite LostFingersIcon;

        public Animator mimicAnimator;

        public GameObject grabPoint;

        public InteractTrigger interactTrigger;

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

            if (player.IsOwner && Vector3.Distance(player.transform.position, this.transform.position) < 10f)
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

        void IHittable.Hit(int force, Vector3 hitDirection, PlayerControllerB playerWhoHit = null, bool playHitSFX = false)
        {
            MimicNetworker.Instance.MimicAddAnger(1, mimic.mimicIndex);
        }
    }
}