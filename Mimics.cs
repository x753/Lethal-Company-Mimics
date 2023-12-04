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

namespace Mimics
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class Mimics : BaseUnityPlugin
    {
        private const string modGUID = "x753.Mimics";
        private const string modName = "Mimics";
        private const string modVersion = "1.0.0";

        private readonly Harmony harmony = new Harmony(modGUID);

        private static Mimics Instance;

        public static GameObject MimicPrefab;
        public static GameObject MimicNetworkerPrefab;

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
                                if (otherTile.name.Contains("Catwalk") || otherTile.name.Contains("LargeForkTile") || otherTile.name.Contains("ElevatorConnector") || (otherTile.name.Contains("StartRoom") && !otherTile.name.Contains("Manor")))
                                {
                                    badPosition = true; // LargeForkTileB has way bigger bounds than it should which kills some mimics it shouldn't
                                }
                            }
                            if (otherTile.Placement.Bounds.IntersectRay(new Ray() { origin = mimicLocation, direction = Vector3.down }))
                            {
                                if (otherTile.name.Contains("MediumRoomHallway1B") || otherTile.name.Contains("LargeForkTile") || otherTile.name.Contains("4x4") || otherTile.name.Contains("ElevatorConnector") || otherTile.name.Contains("StartRoom"))
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
                    GameObject alleyExitDoorContainer = doorway.GetComponentInChildren<SpawnSyncedObject>(true).transform.parent.gameObject;

                    if (StartOfRound.Instance.randomMapSeed % 3 < mIndex && StartOfRound.Instance.randomMapSeed % 753 != 0)
                    {
                        return; // only spawn between 1 and 3 mimics, unless we have a multiple of 753 seed, in which case spawn them all
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

                    GameObject mimic = Instantiate<GameObject>(MimicPrefab, doorway.transform);
                    mimic.transform.position = alleyExitDoorContainer.transform.position;
                    MimicDoor mimicDoor = mimic.GetComponent<MimicDoor>();

                    foreach (AudioSource audio in mimic.GetComponentsInChildren<AudioSource>(true))
                    {
                        audio.outputAudioMixerGroup = StartOfRound.Instance.ship3DAudio.outputAudioMixerGroup;
                    }

                    // Sometimes we need to spawn a mimic near spawn for testing
                    //if (mIndex == 0)
                    //    mimic.transform.position = new Vector3(-7f, 0f, -10f);

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

                    // Randomly assign an imperfection to the mimic
                    int imperfectionIndex = (StartOfRound.Instance.randomMapSeed + mIndex * 2) % 10; // get last digit
                    switch (imperfectionIndex)
                    {
                        case 1:
                        case 2:
                            light.colorTemperature += (10 + imperfectionIndex) * 200;
                            break;

                        case 3:
                        case 4:
                            mimic.GetComponentsInChildren<MeshRenderer>()[0].material.color = new Color(0.489f, 0.1415526f + ((7 + imperfectionIndex) / 100f), 0.1479868f);
                            mimic.GetComponentsInChildren<MeshRenderer>()[1].material.color = new Color(0.489f, 0.1415526f + ((7 + imperfectionIndex) / 100f), 0.1479868f);
                            break;

                        case 5:
                        case 6:
                            mimicDoor.interactTrigger.timeToHold = 1.7f;
                            break;

                        case 7:
                        case 8:
                            mimicDoor.interactTrigger.hoverTip = "Feed : [LMB]";
                            mimicDoor.interactTrigger.holdTip = "Feed : [LMB]";
                            break;

                        case 9:
                        case 0:
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

        public void MimicAttack(int playerId, int mimicIndex)
        {
            if (base.IsOwner)
            {
                MimicNetworker.Instance.MimicAttackClientRpc(playerId, mimicIndex);
            }
            else
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
    }

    public class MimicDoor : MonoBehaviour
    {
        public GameObject playerTarget;

        public BoxCollider frameBox;

        public Animator mimicAnimator;

        public GameObject grabPoint;

        public InteractTrigger interactTrigger;

        private bool attacking = false;

        public static List<MimicDoor> allMimics;
        public int mimicIndex;

        private void Awake()
        {
        }

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

            if (player.deadBody != null)
            {
                player.deadBody = null;
            }

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

            if (player.IsOwner)
            {
                player.KillPlayer(Vector3.zero, true, CauseOfDeath.Unknown, 0);
            }

            float startTime = Time.timeSinceLevelLoad;
            yield return new WaitUntil(() => player.deadBody != null || Time.timeSinceLevelLoad - startTime > 4f);

            if (player.deadBody != null)
            {
                player.deadBody.attachedTo = grabPoint.transform;
                player.deadBody.attachedLimb = player.deadBody.bodyParts[6];
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
                player.deadBody.gameObject.SetActive(false);
            }

            yield return new WaitForSeconds(5f);
            attacking = false;
            interactTrigger.interactable = true;
            yield break;
        }
    }
}