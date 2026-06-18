// PoopSpawnerMod.cs
// Unofficial MelonLoader mod for I Am Cat.
// Spawns a clone of the in-game BigPoop object and initializes it for interaction.
//
// This source file does not include game files, extracted assets, or decompiled game code.
//
// Build notes:
// - Requires unsafe compilation.
// - Requires UnityEngine.XRModule.dll.
// - Requires Il2CppInterop.Runtime.dll.
// - If MelonInfo/MelonGame attributes are defined in another file, remove the assembly lines below.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;
using Il2CppInterop.Runtime;

[assembly: MelonInfo(typeof(PoopSpawnerMod.IAmCatPoopSpawnerMod), "IAmCatPoopSpawnerMod", "1.0.0", "VR-CATMAN")]
[assembly: MelonGame(null, null)]

namespace PoopSpawnerMod
{
    public class IAmCatPoopSpawnerMod : MelonMod
    {
        private bool _prevRightPrimaryButton;

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("[IAmCatPoopSpawnerMod] Loaded");
            MelonLogger.Msg("[IAmCatPoopSpawnerMod] Press Right Controller Primary Button.");
        }

        public override void OnUpdate()
        {
            bool currentRightPrimaryButton = VRInputHelper.GetRightPrimaryButton();

            if (currentRightPrimaryButton && !_prevRightPrimaryButton)
            {
                MelonLogger.Msg("[IAmCatPoopSpawnerMod] Right Primary Button pressed.");
                PoopSpawnerCore.SpawnAndInject();
            }

            _prevRightPrimaryButton = currentRightPrimaryButton;

            // Maintain spawned poop physics and invoke Break() after touch/grab detection.
            PoopSpawnerCore.MaintainSpawnedBigPoopRootPhysicsAndBreakLogic();
        }
    }

    public static class VRInputHelper
    {
        public static bool GetRightPrimaryButton()
        {
            try
            {
                var device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

                if (!device.isValid)
                {
                    return false;
                }

                bool pressed = false;

                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out pressed))
                {
                    return pressed;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[VRInputHelper] Failed to read right primary button");
                MelonLogger.Warning(ex.ToString());
            }

            return false;
        }
    }

    public static class PoopSpawnerCore
    {
        private class Candidate
        {
            public GameObject GameObject;
            public string Path;
            public int Score;
            public float Distance;
        }

        private class SpawnedBigPoopState
        {
            public GameObject Root;
            public IntPtr ContainerPtr;
            public float SpawnTime;
            public bool BreakInvoked;
            public float NextPostBreakMaintenanceTime;
        }

        private static readonly List<SpawnedBigPoopState> SpawnedBigPoopRoots = new List<SpawnedBigPoopState>();
        private const bool DebugLogging = false;
        private static float _nextMaintenanceTime;
        private const float MaintenanceIntervalSeconds = 0.20f;
        private const float IgnoreTouchSecondsAfterSpawn = 0.50f;
        private const float PostBreakPartsMaintenanceIntervalSeconds = 0.25f;

        public static void SpawnAndInject()
        {
            try
            {
                IntPtr containerPtr = FindDiContainerPointer();

                if (containerPtr == IntPtr.Zero)
                {
                    MelonLogger.Warning("[PoopSpawner] DiContainer pointer not found.");
                    MelonLogger.Warning("[PoopSpawner] Cannot call InjectGameObject.");
                    return;
                }

                if (DebugLogging) MelonLogger.Msg("[PoopSpawner] DiContainer pointer found: 0x" + containerPtr.ToString("X"));

                GameObject source = FindBestBigPoopRootSource();

                if (source == null)
                {
                    MelonLogger.Warning("[PoopSpawner] No suitable BigPoop root source found.");
                    return;
                }

                if (DebugLogging) MelonLogger.Msg("[PoopSpawner] Selected source: " + GetPath(source.transform));
                if (DebugLogging) DumpBasicSummary(source, "[Source]");
                if (DebugLogging) DumpImportantComponents(source, "[Source]");

                Vector3 spawnPos;
                Quaternion spawnRot;

                var cam = Camera.main;

                if (cam != null)
                {
                    spawnPos = cam.transform.position + cam.transform.forward * 0.9f + Vector3.up * 0.05f;
                    spawnRot = source.transform.rotation;
                }
                else
                {
                    spawnPos = source.transform.position + Vector3.up * 0.5f;
                    spawnRot = source.transform.rotation;
                }

                // Regular Unity Instantiate.
                GameObject clone = UnityEngine.Object.Instantiate(source, spawnPos, spawnRot);
                clone.name = "Modded_NativeInjectedBigPoopRoot_Clone";
                clone.SetActive(true);

                if (DebugLogging) MelonLogger.Msg("[PoopSpawner] Object.Instantiate completed.");
                if (DebugLogging) MelonLogger.Msg("[PoopSpawner] Clone path: " + GetPath(clone.transform));
                if (DebugLogging) DumpBasicSummary(clone, "[Clone before inject]");
                if (DebugLogging) DumpImportantComponents(clone, "[Clone before inject]");
                if (DebugLogging) DumpPhysicsSummary(clone, "[Clone before inject]");

                bool injected = InvokeInjectGameObject(containerPtr, clone);

                if (injected)
                {
                    MelonLogger.Msg("[PoopSpawner] InjectGameObject(clone) invoked successfully.");
                }
                else
                {
                    MelonLogger.Warning("[PoopSpawner] InjectGameObject(clone) failed.");
                }

                // Keep the cloned root Rigidbody dynamic so it does not freeze in the air.
                ForceBigPoopRootRigidbodyDynamic(clone, silent: false);
                RegisterSpawnedBigPoopRoot(clone, containerPtr);

                if (DebugLogging) DumpBasicSummary(clone, "[Clone after inject + dynamic root]");
                if (DebugLogging) DumpImportantComponents(clone, "[Clone after inject + dynamic root]");
                if (DebugLogging) DumpPhysicsSummary(clone, "[Clone after inject + dynamic root]");

                MelonLogger.Msg("[PoopSpawner] Done. Check if the clone is grabbable.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[PoopSpawner] SpawnAndInject failed.");
                MelonLogger.Error(ex.ToString());
            }
        }

        private static void RegisterSpawnedBigPoopRoot(GameObject clone, IntPtr containerPtr)
        {
            if (clone == null) return;

            SpawnedBigPoopRoots.Add(new SpawnedBigPoopState
            {
                Root = clone,
                ContainerPtr = containerPtr,
                SpawnTime = Time.time,
                BreakInvoked = false,
                NextPostBreakMaintenanceTime = 0f
            });

            MelonLogger.Msg("[PoopSpawner] Registered spawned BigPoop root for root Rigidbody maintenance and break logic: " + clone.name);
        }

        public static void MaintainSpawnedBigPoopRootPhysicsAndBreakLogic()
        {
            try
            {
                if (Time.time < _nextMaintenanceTime)
                {
                    return;
                }

                _nextMaintenanceTime = Time.time + MaintenanceIntervalSeconds;

                for (int i = SpawnedBigPoopRoots.Count - 1; i >= 0; i--)
                {
                    var state = SpawnedBigPoopRoots[i];

                    if (state == null || state.Root == null)
                    {
                        SpawnedBigPoopRoots.RemoveAt(i);
                        continue;
                    }

                    if (!state.BreakInvoked)
                    {
                        // Keep the root BigPoop from freezing in the air.
                        ForceBigPoopRootRigidbodyDynamic(state.Root, silent: true);

                        // Ignore copied SurfaceMarker state briefly after spawn.
                        if (Time.time - state.SpawnTime >= IgnoreTouchSecondsAfterSpawn)
                        {
                            if (IsSurfaceMarkerTouchedOrGrabbed(state.Root))
                            {
                                MelonLogger.Msg("[PoopSpawner] Touch/grab detected on cloned BigPoop. Invoking Breakable.Break(). root=" + state.Root.name);
                                InvokeBreakOnBreakables(state);

                                state.BreakInvoked = true;
                                state.NextPostBreakMaintenanceTime = Time.time + 0.15f;
                            }
                        }
                    }
                    else
                    {
                        // After Break(), keep newly active Poop parts physically interactive.
                        if (Time.time >= state.NextPostBreakMaintenanceTime)
                        {
                            state.NextPostBreakMaintenanceTime = Time.time + PostBreakPartsMaintenanceIntervalSeconds;
                            ForceActiveBreakPartsPhysics(state.Root, silent: true);
                        }
                    }
                }
            }
            catch
            {
                // Suppress repeated maintenance errors to avoid log spam.
            }
        }

        private static bool IsSurfaceMarkerTouchedOrGrabbed(GameObject root)
        {
            if (root == null) return false;

            try
            {
                var surfaceMarker = FindFirstComponentByIl2CppName(root, "SurfaceMarker");

                if (surfaceMarker == null)
                {
                    return false;
                }

                bool isPawTouched = ReadIl2CppBoolField(surfaceMarker, "IsPawTuched");
                bool isBodyTouched = ReadIl2CppBoolField(surfaceMarker, "IsBodyTuched");
                bool isGrabbed = ReadIl2CppBoolField(surfaceMarker, "IsGrabbed");
                bool isMouthGrabbed = ReadIl2CppBoolField(surfaceMarker, "IsMouthGrabbed");

                if (isPawTouched || isBodyTouched || isGrabbed || isMouthGrabbed)
                {
                    MelonLogger.Msg(
                        "[PoopSpawner] SurfaceMarker state: " +
                        "IsPawTuched=" + isPawTouched +
                        " IsBodyTuched=" + isBodyTouched +
                        " IsGrabbed=" + isGrabbed +
                        " IsMouthGrabbed=" + isMouthGrabbed
                    );

                    return true;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PoopSpawner] IsSurfaceMarkerTouchedOrGrabbed failed.");
                MelonLogger.Warning(ex.ToString());
            }

            return false;
        }

        private static void InvokeBreakOnBreakables(SpawnedBigPoopState state)
        {
            if (state == null || state.Root == null) return;

            try
            {
                var breakables = FindComponentsByIl2CppName(state.Root, "Breakable");

                MelonLogger.Msg("[PoopSpawner] Breakable components on root: " + breakables.Count);

                if (breakables.Count == 0)
                {
                    MelonLogger.Warning("[PoopSpawner] No Breakable components found on cloned BigPoop root.");
                    return;
                }

                int invokedCount = 0;

                for (int i = 0; i < breakables.Count; i++)
                {
                    var breakable = breakables[i];
                    if (breakable == null) continue;

                    IntPtr klass = IL2CPP.il2cpp_object_get_class(breakable.Pointer);
                    IntPtr breakMethod = FindMethod(klass, "Break", 0);

                    if (breakMethod == IntPtr.Zero)
                    {
                        MelonLogger.Warning("[PoopSpawner] Break() method not found on Breakable[" + i + "]");
                        continue;
                    }

                    IntPtr exception = IntPtr.Zero;
                    RuntimeInvokeNoArgs(breakMethod, breakable.Pointer, ref exception);

                    if (exception != IntPtr.Zero)
                    {
                        MelonLogger.Warning("[PoopSpawner] Exception thrown by Breakable.Break() on Breakable[" + i + "]. exception ptr=0x" + exception.ToString("X"));
                        continue;
                    }

                    invokedCount++;
                    MelonLogger.Msg("[PoopSpawner] Breakable.Break() invoked on Breakable[" + i + "]");
                }

                MelonLogger.Msg("[PoopSpawner] Break invoked count: " + invokedCount);

                // After Break(), child Poop objects may become active. Re-inject the root once.
                if (state.ContainerPtr != IntPtr.Zero)
                {
                    bool injected = InvokeInjectGameObject(state.ContainerPtr, state.Root);

                    if (injected)
                    {
                        MelonLogger.Msg("[PoopSpawner] Post-break InjectGameObject(root) invoked successfully.");
                    }
                    else
                    {
                        MelonLogger.Warning("[PoopSpawner] Post-break InjectGameObject(root) failed.");
                    }
                }

                ForceActiveBreakPartsPhysics(state.Root, silent: false);
                if (DebugLogging) DumpPhysicsSummary(state.Root, "[Clone after Break]");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PoopSpawner] InvokeBreakOnBreakables failed.");
                MelonLogger.Warning(ex.ToString());
            }
        }

        private static void ForceBigPoopRootRigidbodyDynamic(GameObject root, bool silent)
        {
            if (root == null) return;

            try
            {
                // Only touch the root Rigidbody before Break().
                var rb = root.GetComponent<Rigidbody>();

                if (rb == null)
                {
                    if (!silent)
                    {
                        MelonLogger.Warning("[PoopSpawner] Root Rigidbody not found on " + root.name);
                    }

                    return;
                }

                bool beforeKinematic = rb.isKinematic;
                bool beforeUseGravity = rb.useGravity;
                bool beforeDetectCollisions = rb.detectCollisions;

                rb.isKinematic = false;
                rb.useGravity = true;
                rb.detectCollisions = true;
                rb.WakeUp();

                if (!silent)
                {
                    MelonLogger.Msg(
                        "[PoopSpawner] Root Rigidbody dynamicized: " +
                        "path=" + GetPath(rb.transform) +
                        " isKinematic " + beforeKinematic + " -> " + rb.isKinematic +
                        " useGravity " + beforeUseGravity + " -> " + rb.useGravity +
                        " detectCollisions " + beforeDetectCollisions + " -> " + rb.detectCollisions +
                        " mass=" + rb.mass
                    );
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MelonLogger.Warning("[PoopSpawner] ForceBigPoopRootRigidbodyDynamic failed.");
                    MelonLogger.Warning(ex.ToString());
                }
            }
        }

        private static void ForceActiveBreakPartsPhysics(GameObject root, bool silent)
        {
            if (root == null) return;

            try
            {
                var parts = root.transform.Find("Parts");

                if (parts == null)
                {
                    if (!silent)
                    {
                        MelonLogger.Warning("[PoopSpawner] Parts child not found under " + root.name);
                    }

                    return;
                }

                int enabledColliderCount = 0;
                int dynamicRbCount = 0;

                for (int i = 0; i < parts.childCount; i++)
                {
                    var child = parts.GetChild(i);
                    if (child == null) continue;

                    var childGo = child.gameObject;
                    if (childGo == null) continue;

                    // Only modify active Poop pieces after Break().
                    if (!childGo.activeInHierarchy)
                    {
                        continue;
                    }

                    if (!childGo.name.StartsWith("Poop"))
                    {
                        continue;
                    }

                    var colliders = childGo.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < colliders.Length; c++)
                    {
                        var col = colliders[c];
                        if (col == null) continue;

                        if (!col.enabled)
                        {
                            col.enabled = true;
                            enabledColliderCount++;
                        }
                    }

                    var rb = childGo.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        bool changed = false;

                        if (rb.isKinematic)
                        {
                            rb.isKinematic = false;
                            changed = true;
                        }

                        if (!rb.useGravity)
                        {
                            rb.useGravity = true;
                            changed = true;
                        }

                        if (!rb.detectCollisions)
                        {
                            rb.detectCollisions = true;
                            changed = true;
                        }

                        rb.WakeUp();

                        if (changed)
                        {
                            dynamicRbCount++;
                        }
                    }
                }

                if (!silent)
                {
                    MelonLogger.Msg("[PoopSpawner] ForceActiveBreakPartsPhysics done. enabledColliderCount=" + enabledColliderCount + " dynamicRbCount=" + dynamicRbCount);
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MelonLogger.Warning("[PoopSpawner] ForceActiveBreakPartsPhysics failed.");
                    MelonLogger.Warning(ex.ToString());
                }
            }
        }

        private static Component FindFirstComponentByIl2CppName(GameObject root, string shortName)
        {
            var list = FindComponentsByIl2CppName(root, shortName);
            if (list.Count == 0) return null;
            return list[0];
        }

        private static List<Component> FindComponentsByIl2CppName(GameObject root, string shortName)
        {
            var result = new List<Component>();

            if (root == null)
            {
                return result;
            }

            try
            {
                var comps = root.GetComponents<Component>();

                for (int i = 0; i < comps.Length; i++)
                {
                    var comp = comps[i];
                    if (comp == null) continue;

                    string name = "";

                    try
                    {
                        var t = comp.GetIl2CppType();
                        if (t != null) name = t.FullName;
                    }
                    catch
                    {
                        name = GetNativeClassNameOfObject(comp);
                    }

                    if (name == shortName || name.EndsWith("." + shortName))
                    {
                        result.Add(comp);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return result;
        }

        private static bool ReadIl2CppBoolField(Component comp, string fieldName)
        {
            if (comp == null || comp.Pointer == IntPtr.Zero)
            {
                return false;
            }

            IntPtr klass = IL2CPP.il2cpp_object_get_class(comp.Pointer);
            if (klass == IntPtr.Zero) return false;

            IntPtr field = FindField(klass, fieldName);
            if (field == IntPtr.Zero) return false;

            try
            {
                int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                IntPtr address = IntPtr.Add(comp.Pointer, offset);

                byte value = Marshal.ReadByte(address);
                return value != 0;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr FindField(IntPtr klass, string fieldName)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                IntPtr iter = IntPtr.Zero;

                while (true)
                {
                    IntPtr field = IL2CPP.il2cpp_class_get_fields(klass, ref iter);

                    if (field == IntPtr.Zero)
                    {
                        break;
                    }

                    string name = PtrToStringAnsiSafe(IL2CPP.il2cpp_field_get_name(field));

                    if (name == fieldName)
                    {
                        return field;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindDiContainerPointer()
        {
            // 1. GameSystems/SceneContext
            try
            {
                var go = GameObject.Find("GameSystems/SceneContext");

                if (go != null)
                {
                    MelonLogger.Msg("[PoopSpawner] Found GameObject: GameSystems/SceneContext");

                    var container = TryGetContainerFromGameObject(go);

                    if (container != IntPtr.Zero)
                    {
                        return container;
                    }
                }
                else
                {
                    MelonLogger.Msg("[PoopSpawner] GameObject.Find(\"GameSystems/SceneContext\") returned null.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PoopSpawner] GameSystems/SceneContext lookup failed.");
                MelonLogger.Warning(ex.ToString());
            }

            // 2. Search for GameObjects that look like SceneContext.
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GameObject>();

                for (int i = 0; i < all.Length; i++)
                {
                    var go = all[i];
                    if (go == null || go.transform == null) continue;

                    string path = GetPath(go.transform);

                    if (!go.name.Contains("SceneContext") && !path.Contains("SceneContext"))
                    {
                        continue;
                    }

                    if (DebugLogging) MelonLogger.Msg("[PoopSpawner] Trying SceneContext-like GameObject: " + path);

                    var container = TryGetContainerFromGameObject(go);

                    if (container != IntPtr.Zero)
                    {
                        return container;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PoopSpawner] SceneContext-like search failed.");
                MelonLogger.Warning(ex.ToString());
            }

            // 3. Search all components for a native SceneContext name.
            try
            {
                var allComponents = Resources.FindObjectsOfTypeAll<Component>();

                for (int i = 0; i < allComponents.Length; i++)
                {
                    var comp = allComponents[i];
                    if (comp == null) continue;

                    string native = GetNativeClassNameOfObject(comp);

                    if (native == "SceneContext" || native.EndsWith(".SceneContext"))
                    {
                        MelonLogger.Msg("[PoopSpawner] Found SceneContext Component by full component scan: " + GetPath(comp.transform));
                        var container = TryGetContainerFromComponent(comp);

                        if (container != IntPtr.Zero)
                        {
                            return container;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PoopSpawner] Component scan for SceneContext failed.");
                MelonLogger.Warning(ex.ToString());
            }

            return IntPtr.Zero;
        }

        private static IntPtr TryGetContainerFromGameObject(GameObject go)
        {
            if (go == null) return IntPtr.Zero;

            var comps = go.GetComponents<Component>();

            for (int i = 0; i < comps.Length; i++)
            {
                var comp = comps[i];
                if (comp == null) continue;

                string native = GetNativeClassNameOfObject(comp);
                if (DebugLogging) MelonLogger.Msg("[PoopSpawner] SceneContext candidate component: " + native);

                if (native == "SceneContext" || native.EndsWith(".SceneContext"))
                {
                    return TryGetContainerFromComponent(comp);
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr TryGetContainerFromComponent(Component sceneContextComponent)
        {
            if (sceneContextComponent == null) return IntPtr.Zero;

            string native = GetNativeClassNameOfObject(sceneContextComponent);
            if (DebugLogging) MelonLogger.Msg("[PoopSpawner] Trying get_Container on component: " + native);

            IntPtr sceneContextPtr = sceneContextComponent.Pointer;

            if (sceneContextPtr == IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] SceneContext component pointer is zero.");
                return IntPtr.Zero;
            }

            IntPtr sceneContextClass = IL2CPP.il2cpp_object_get_class(sceneContextPtr);

            if (sceneContextClass == IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] SceneContext class pointer is zero.");
                return IntPtr.Zero;
            }

            IntPtr getContainerMethod = FindMethod(sceneContextClass, "get_Container", 0);

            if (getContainerMethod == IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] get_Container method not found on SceneContext.");
                if (DebugLogging) DumpMethods(sceneContextClass, "[PoopSpawner SceneContext methods]", 80);
                return IntPtr.Zero;
            }

            if (DebugLogging) MelonLogger.Msg("[PoopSpawner] get_Container method found.");

            IntPtr exception = IntPtr.Zero;
            IntPtr containerPtr = RuntimeInvokeNoArgs(getContainerMethod, sceneContextPtr, ref exception);

            if (exception != IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] Exception thrown by get_Container. exception ptr=0x" + exception.ToString("X"));
                return IntPtr.Zero;
            }

            if (containerPtr == IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] get_Container returned null.");
                return IntPtr.Zero;
            }

            string containerClassName = GetNativeClassNameFromObjectPtr(containerPtr);
            if (DebugLogging) MelonLogger.Msg("[PoopSpawner] get_Container returned object: " + containerClassName);

            return containerPtr;
        }

        private static bool InvokeInjectGameObject(IntPtr containerPtr, GameObject clone)
        {
            if (containerPtr == IntPtr.Zero || clone == null)
            {
                return false;
            }

            IntPtr containerClass = IL2CPP.il2cpp_object_get_class(containerPtr);

            if (containerClass == IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] Container class pointer is zero.");
                return false;
            }

            if (DebugLogging) MelonLogger.Msg("[PoopSpawner] Container class: " + GetNativeClassNameFromClassPtr(containerClass));

            IntPtr method = FindMethod(containerClass, "InjectGameObject", 1);

            if (method == IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] InjectGameObject method not found with 1 parameter.");
                if (DebugLogging) DumpMethods(containerClass, "[PoopSpawner DiContainer methods]", 160);
                return false;
            }

            if (DebugLogging) MelonLogger.Msg("[PoopSpawner] InjectGameObject method found: " + SafeGetMethodSignature(method));

            IntPtr exception = IntPtr.Zero;
            RuntimeInvokeOneObjectArg(method, containerPtr, clone.Pointer, ref exception);

            if (exception != IntPtr.Zero)
            {
                MelonLogger.Warning("[PoopSpawner] Exception thrown by InjectGameObject. exception ptr=0x" + exception.ToString("X"));
                return false;
            }

            return true;
        }

        private static GameObject FindBestBigPoopRootSource()
        {
            var candidates = new List<Candidate>();
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            var cam = Camera.main;

            for (int i = 0; i < all.Length; i++)
            {
                var go = all[i];
                if (go == null) continue;

                if (go.name.StartsWith("Modded_")) continue;

                var t = go.transform;
                if (t == null) continue;

                string path = GetPath(t);
                string name = go.name;

                // Use only the BigPoop root as source. Do not clone already-broken Poop pieces.
                bool isBigPoopRoot =
                    (name == "BigPoop" || name.StartsWith("BigPoop ")) &&
                    path.Contains("/Food/Poop/") &&
                    !path.Contains("/Parts/") &&
                    go.activeInHierarchy;

                if (!isBigPoopRoot)
                {
                    continue;
                }

                int score = 0;

                score += 1000; // Strong base score because this is limited to BigPoop roots.

                if (go.activeSelf) score += 30;
                if (go.activeInHierarchy) score += 100;

                if (HasComponentIl2CppName(go, "SurfaceMarker")) score += 120;
                if (HasComponentIl2CppName(go, "ObjectActivityController")) score += 100;
                if (HasComponentIl2CppName(go, "Food")) score += 80;
                if (HasComponentIl2CppName(go, "Breakable")) score += 80;

                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) score += 80;

                var colliders = go.GetComponentsInChildren<Collider>(true);
                int enabledColliderCount = 0;

                for (int c = 0; c < colliders.Length; c++)
                {
                    if (colliders[c] != null && colliders[c].enabled)
                    {
                        enabledColliderCount++;
                    }
                }

                if (enabledColliderCount > 0) score += 80;

                float distance = 9999f;

                if (cam != null)
                {
                    distance = Vector3.Distance(cam.transform.position, go.transform.position);
                }

                if (distance < 2f) score += 40;
                else if (distance < 5f) score += 20;

                candidates.Add(new Candidate
                {
                    GameObject = go,
                    Path = path,
                    Score = score,
                    Distance = distance
                });
            }

            candidates.Sort((a, b) =>
            {
                int scoreCompare = b.Score.CompareTo(a.Score);
                if (scoreCompare != 0) return scoreCompare;

                return a.Distance.CompareTo(b.Distance);
            });

            if (DebugLogging)
            {
                MelonLogger.Msg("[PoopSpawner] BigPoop root source candidate count: " + candidates.Count);

                int dumpCount = candidates.Count;
                if (dumpCount > 10) dumpCount = 10;

                for (int i = 0; i < dumpCount; i++)
                {
                    MelonLogger.Msg(
                        "[PoopSpawner] BigPoopCandidate[" + i + "] " +
                        "score=" + candidates[i].Score +
                        " dist=" + candidates[i].Distance.ToString("F2") +
                        " path=" + candidates[i].Path
                    );
                }
            }

            if (candidates.Count == 0)
            {
                MelonLogger.Warning("[PoopSpawner] No BigPoop root candidate found.");
                MelonLogger.Warning("[PoopSpawner] If BigPoop exists visually, its name/path may differ. Need another dump.");
                return null;
            }

            return candidates[0].GameObject;
        }

        private static bool HasComponentIl2CppName(GameObject go, string shortName)
        {
            if (go == null) return false;

            try
            {
                var comps = go.GetComponents<Component>();

                for (int i = 0; i < comps.Length; i++)
                {
                    var comp = comps[i];
                    if (comp == null) continue;

                    string name = "";

                    try
                    {
                        var t = comp.GetIl2CppType();
                        if (t != null) name = t.FullName;
                    }
                    catch
                    {
                        name = GetNativeClassNameOfObject(comp);
                    }

                    if (name == shortName || name.EndsWith("." + shortName))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static IntPtr FindMethod(IntPtr klass, string methodName, int paramCount)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                IntPtr iter = IntPtr.Zero;

                while (true)
                {
                    IntPtr method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);

                    if (method == IntPtr.Zero)
                    {
                        break;
                    }

                    string name = "";

                    try
                    {
                        name = PtrToStringAnsiSafe(IL2CPP.il2cpp_method_get_name(method));
                    }
                    catch
                    {
                        // ignore
                    }

                    if (name != methodName)
                    {
                        continue;
                    }

                    int count = -1;

                    try
                    {
                        count = (int)IL2CPP.il2cpp_method_get_param_count(method);
                    }
                    catch
                    {
                        // ignore
                    }

                    if (count == paramCount)
                    {
                        return method;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PoopSpawner] FindMethod failed for " + methodName);
                MelonLogger.Warning(ex.ToString());
            }

            return IntPtr.Zero;
        }

        private static void DumpMethods(IntPtr klass, string label, int max)
        {
            if (klass == IntPtr.Zero) return;

            try
            {
                MelonLogger.Msg(label + " start");

                IntPtr iter = IntPtr.Zero;
                int count = 0;

                while (count < max)
                {
                    IntPtr method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);

                    if (method == IntPtr.Zero)
                    {
                        break;
                    }

                    MelonLogger.Msg(label + " Method[" + count + "] " + SafeGetMethodSignature(method));
                    count++;
                }

                MelonLogger.Msg(label + " end count=" + count);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PoopSpawner] DumpMethods failed.");
                MelonLogger.Warning(ex.ToString());
            }
        }

        private static string SafeGetMethodSignature(IntPtr method)
        {
            if (method == IntPtr.Zero) return "(method null)";

            string returnType = "(return?)";
            string methodName = "(methodName?)";
            string parameters = "";

            try
            {
                methodName = PtrToStringAnsiSafe(IL2CPP.il2cpp_method_get_name(method));
            }
            catch
            {
                // ignore
            }

            try
            {
                IntPtr returnTypePtr = IL2CPP.il2cpp_method_get_return_type(method);
                returnType = SafeGetTypeName(returnTypePtr);
            }
            catch
            {
                // ignore
            }

            try
            {
                uint paramCount = IL2CPP.il2cpp_method_get_param_count(method);

                for (uint i = 0; i < paramCount; i++)
                {
                    if (i > 0) parameters += ", ";

                    string paramType = "(paramType?)";
                    string paramName = "";

                    try
                    {
                        IntPtr paramTypePtr = IL2CPP.il2cpp_method_get_param(method, i);
                        paramType = SafeGetTypeName(paramTypePtr);
                    }
                    catch
                    {
                        // ignore
                    }

                    try
                    {
                        paramName = PtrToStringAnsiSafe(IL2CPP.il2cpp_method_get_param_name(method, i));
                    }
                    catch
                    {
                        // ignore
                    }

                    if (!string.IsNullOrEmpty(paramName))
                    {
                        parameters += paramType + " " + paramName;
                    }
                    else
                    {
                        parameters += paramType;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return returnType + " " + methodName + "(" + parameters + ")";
        }

        private static string SafeGetTypeName(IntPtr typePtr)
        {
            if (typePtr == IntPtr.Zero) return "void/null";

            try
            {
                return PtrToStringAnsiSafe(IL2CPP.il2cpp_type_get_name(typePtr));
            }
            catch
            {
                return "(typeName failed)";
            }
        }

        private static void DumpBasicSummary(GameObject go, string label)
        {
            if (go == null) return;

            string tagText;

            try
            {
                tagText = go.tag;
            }
            catch
            {
                tagText = "(tag read failed)";
            }

            MelonLogger.Msg(label + " name=" + go.name);
            MelonLogger.Msg(label + " path=" + GetPath(go.transform));
            MelonLogger.Msg(label + " activeSelf=" + go.activeSelf);
            MelonLogger.Msg(label + " activeInHierarchy=" + go.activeInHierarchy);
            MelonLogger.Msg(label + " layer=" + go.layer);
            MelonLogger.Msg(label + " tag=" + tagText);
            MelonLogger.Msg(label + " childCount=" + go.transform.childCount);
        }

        private static void DumpImportantComponents(GameObject root, string label)
        {
            if (root == null) return;

            var comps = root.GetComponents<Component>();

            MelonLogger.Msg(label + " root component count=" + comps.Length);

            for (int i = 0; i < comps.Length; i++)
            {
                var comp = comps[i];

                if (comp == null)
                {
                    MelonLogger.Msg(label + " Component[" + i + "] null");
                    continue;
                }

                string typeName = "";

                try
                {
                    var t = comp.GetIl2CppType();
                    if (t != null) typeName = t.FullName;
                }
                catch
                {
                    typeName = GetNativeClassNameOfObject(comp);
                }

                MelonLogger.Msg(label + " Component[" + i + "] " + typeName);
            }
        }

        private static void DumpPhysicsSummary(GameObject root, string label)
        {
            if (root == null) return;

            var colliders = root.GetComponentsInChildren<Collider>(true);
            var rigidbodies = root.GetComponentsInChildren<Rigidbody>(true);

            MelonLogger.Msg(label + " Collider count=" + colliders.Length);

            for (int i = 0; i < colliders.Length; i++)
            {
                var c = colliders[i];
                if (c == null) continue;

                MelonLogger.Msg(
                    label +
                    " Collider[" + i + "] path=" + GetPath(c.transform) +
                    " enabled=" + c.enabled +
                    " isTrigger=" + c.isTrigger +
                    " layer=" + c.gameObject.layer
                );
            }

            MelonLogger.Msg(label + " Rigidbody count=" + rigidbodies.Length);

            for (int i = 0; i < rigidbodies.Length; i++)
            {
                var rb = rigidbodies[i];
                if (rb == null) continue;

                MelonLogger.Msg(
                    label +
                    " Rigidbody[" + i + "] path=" + GetPath(rb.transform) +
                    " isKinematic=" + rb.isKinematic +
                    " useGravity=" + rb.useGravity +
                    " detectCollisions=" + rb.detectCollisions +
                    " mass=" + rb.mass
                );
            }
        }

        private static string GetNativeClassNameOfObject(Component comp)
        {
            if (comp == null) return "";

            try
            {
                IntPtr objPtr = comp.Pointer;
                if (objPtr == IntPtr.Zero) return "";

                return GetNativeClassNameFromObjectPtr(objPtr);
            }
            catch
            {
                return "";
            }
        }

        private static string GetNativeClassNameFromObjectPtr(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero) return "";

            try
            {
                IntPtr klass = IL2CPP.il2cpp_object_get_class(objPtr);
                return GetNativeClassNameFromClassPtr(klass);
            }
            catch
            {
                return "";
            }
        }

        private static string GetNativeClassNameFromClassPtr(IntPtr klass)
        {
            if (klass == IntPtr.Zero) return "";

            try
            {
                string ns = PtrToStringAnsiSafe(IL2CPP.il2cpp_class_get_namespace(klass));
                string name = PtrToStringAnsiSafe(IL2CPP.il2cpp_class_get_name(klass));

                if (!string.IsNullOrEmpty(ns))
                {
                    return ns + "." + name;
                }

                return name;
            }
            catch
            {
                return "";
            }
        }

        private static string PtrToStringAnsiSafe(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "";

            try
            {
                string s = Marshal.PtrToStringAnsi(ptr);
                return s ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "";

            string path = t.name;

            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }

            return path;
        }

        private static unsafe IntPtr RuntimeInvokeNoArgs(IntPtr method, IntPtr obj, ref IntPtr exception)
        {
            void** args = null;
            return IL2CPP.il2cpp_runtime_invoke(method, obj, args, ref exception);
        }

        private static unsafe IntPtr RuntimeInvokeOneObjectArg(IntPtr method, IntPtr obj, IntPtr arg0, ref IntPtr exception)
        {
            void** args = stackalloc void*[1];
            args[0] = (void*)arg0;
            return IL2CPP.il2cpp_runtime_invoke(method, obj, args, ref exception);
        }
    }
}
