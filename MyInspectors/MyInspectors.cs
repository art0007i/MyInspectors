using Elements.Core;
using FrooxEngine;
using FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Slots;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using static FrooxEngine.UserInspector;

namespace MyInspectors
{
    public class MyInspectors : ResoniteMod
    {
        public override string Name => "MyInspectors";
        public override string Author => "art0007i";
        public override string Version => "2.1.1";
        public override string Link => "https://github.com/art0007i/MyInspectors/";

        public static ModConfiguration config { get; private set; }

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> KEY_ENABLE = new("enable", "Untick to disable the mod.", () => true);


        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.art0007i.MyInspectors");

            config = GetConfiguration();
            if (config.GetValue(KEY_ENABLE))
            {
                Debug("Applying Patches");
                harmony.PatchAll();
            }

            config.OnThisConfigurationChanged += e =>
            {
                if (e.Key == KEY_ENABLE)
                {
                    if (e.Config.GetValue(KEY_ENABLE))
                    {
                        Debug("Applying Patches");
                        harmony.PatchAll();
                    }
                    else
                    {
                        Debug("Removing Patches");
                        harmony.UnpatchAll("me.art0007i.MyInspectors");
                    }
                }
            };
        }
        static FieldInfo _targetContainer = AccessTools.Field(typeof(WorkerInspector), "_targetContainer");
        static ConditionalWeakTable<UserInspector, UserInspectorState> userInspectorStates = new();
        // patching 'hot' code. but like idk how else to do it
        [HarmonyPatch(typeof(SceneInspector), "OnAwake")]
        class StupidInspectorFixupPatch
        {
            public static void Postfix(SceneInspector __instance)
            {
                // onchanges triggers 1 update later, when we need 0 updates so it doesn't sync
                __instance.RunInUpdates(0, () =>
                {
                    AccessTools.Method(typeof(SceneInspector), "OnChanges").Invoke(__instance, null);
                });
            }
        }

        [HarmonyPatch]
        class SceneInspector_Patch
        {
            public static IEnumerable<MethodInfo> TargetMethods()
            {
                yield return AccessTools.Method(typeof(WorkerInspector), "OnChanges");
                yield return AccessTools.Method(typeof(SceneInspector), "OnChanges");
            }

            public static bool IsChangedLocally(ConflictingSyncElement el) => el.LastConfirmedTime == el.LastHostVersion || el.IsSyncDirty;

            public static bool ShouldContinue(Worker worker)
            {
                if (worker is SceneInspector sceneIn)
                {
                    return IsChangedLocally(sceneIn.ComponentView) || IsChangedLocally(sceneIn.Root);
                }

                if (worker is WorkerInspector workIn && _targetContainer.GetValue(workIn) is SyncRef<Worker> target)
                {
                    // doing this is weird, because we won't unregister events whenever you set a workerinspector to null.
                    // but also this never happens by default so hope it's ok
                    return target.Target != null && IsChangedLocally(target);
                }

                return false;
            }

            public static void ResetContainer(WorkerInspector i)
            {
                if (i.World.IsAuthority) return;

                if (_targetContainer.GetValue(i) is SyncRef<Worker> worker)
                {
                    worker.Target = null;
                }
            }

            public static bool IsHost(SceneInspector i) => i.World.IsAuthority;

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {

                var codes = instructions.ToList();
                var rootField = AccessTools.Field(typeof(SceneInspector), "Root");
                var compField = AccessTools.Field(typeof(SceneInspector), "ComponentView");
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];

                    if (code.opcode == OpCodes.Beq && i >= 2)
                    {
                        var twoBefore = codes[i - 2];
                        if (twoBefore.operand is FieldInfo f && (f == rootField || f == compField))
                        {
                            // if (IsHost | IsChangedLocally) & !(original statement)
                            yield return new CodeInstruction(OpCodes.Ceq);
                            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                            yield return new CodeInstruction(OpCodes.Ceq);
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SceneInspector_Patch), nameof(IsHost)));
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldfld, twoBefore.operand);
                            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SceneInspector_Patch), nameof(IsChangedLocally)));
                            yield return new CodeInstruction(OpCodes.Or);
                            yield return new CodeInstruction(OpCodes.And);

                            code.opcode = OpCodes.Brfalse;
                        }
                    }

                    yield return code;

                    if (code.Calls(AccessTools.PropertyGetter(typeof(World), "IsAuthority")))
                    {
                        // is host OR the value has been changed by local user
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SceneInspector_Patch), nameof(ShouldContinue)));
                        yield return new CodeInstruction(OpCodes.Or);
                    }

                    if (code.StoresField(AccessTools.Field(typeof(WorkerInspector), "_currentContainer")))
                    {
                        // reset the container so others don't know about it
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SceneInspector_Patch), nameof(ResetContainer)));
                    }
                }
            }
        }
        static void createPatchedSyncref<T>(ref SyncRef<T> old, IWorldElement existingObj) where T : class, IWorldElement
        {//old.World.IsAuthority does not exist yet
            if (!existingObj.World.IsAuthority) old = new PatchedSyncRef<T>();
        }
        [HarmonyPatch]
        private class InitializeSyncMemberPatch
        {
            [HarmonyPostfix]
            [HarmonyPatch(typeof(SlotInspector), "InitializeSyncMembers")]
            static void SlotInspectorPostfix(SlotInspector __instance, ref SyncRef<Slot> ____rootSlot) => createPatchedSyncref(ref ____rootSlot, __instance);

            [HarmonyPostfix]
            [HarmonyPatch(typeof(UserInspectorItem), "InitializeSyncMembers")]
            static void UserInspectorItemPostfix(UserInspectorItem __instance, ref SyncRef<User> ____user) => createPatchedSyncref(ref ____user, __instance);

            [HarmonyPostfix]
            [HarmonyPatch(typeof(BagEditor), "InitializeSyncMembers")]
            static void BagEditorPostfix(BagEditor __instance, ref SyncRef<ISyncBag> ____targetBag) => createPatchedSyncref(ref ____targetBag, __instance);

            [HarmonyPostfix]
            [HarmonyPatch(typeof(ListEditor), "InitializeSyncMembers")]
            static void ListEditorPostfix(ListEditor __instance, ref SyncRef<ISyncList> ____targetList) => createPatchedSyncref(ref ____targetList, __instance);
        }

        static bool ShouldBuild(SyncField<RefID> field)
        {
            if (field.World.IsAuthority) return true;
            return field is EditorTargetField patched && patched.ShouldBuild;
        }

        static MethodInfo ShouldBuildInfo = AccessTools.Method(typeof(MyInspectors), nameof(ShouldBuild));
        static IEnumerable<CodeInstruction> Editor_OnChanges_Transpiler(IEnumerable<CodeInstruction> codes, FieldInfo targetField)
        {
            int hit = 0;
            foreach (var code in codes)
            {
                if (hit <= 0 && code.Calls(AccessTools.PropertyGetter(typeof(Worker), "World"))) // (code.operand as MethodInfo)?.Name == "get_World")
                {
                    hit++;
                    yield return new(OpCodes.Ldfld, targetField);
                    yield return new(OpCodes.Call, ShouldBuildInfo);
                }
                else if (hit == 1)
                {
                    hit++; // skip an instruction in codes
                }
                else
                {
                    yield return code;
                }
            }
        }

        [HarmonyPatch(typeof(SlotInspector), "OnChanges")]
        class SlotInspector_OnChanges_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => Editor_OnChanges_Transpiler(codes, AccessTools.Field(typeof(SlotInspector), "_rootSlot"));

            static void Postfix(SyncRef<TextExpandIndicator> ____expanderIndicator, SyncRef<Slot> ____rootSlot)
            {
                if (____expanderIndicator.World.IsAuthority) return;

                TextExpandIndicator textexp = ____expanderIndicator.Target;
                if (textexp == null || textexp.Empty.IsLinked) return;
                textexp.CustomEmptyCheck.Target = null; // use default empty check
                var logixSlot = textexp.Slot;
                var stringNodeEmpty = logixSlot.AttachComponent<ValueObjectInput<string>>();
                var stringNodeClosed = logixSlot.AttachComponent<ObjectValueSource<string>>();
                var childCountNode = logixSlot.AttachComponent<ChildrenCount>();
                var equalsNode = logixSlot.AttachComponent<ValueEquals<int>>();
                var conditionalNode = logixSlot.AttachComponent<ObjectConditional<string>>();
                var slotRef = logixSlot.AttachComponent<ElementSource<Slot>>();
                var driverNode = logixSlot.AttachComponent<ObjectFieldDrive<string>>();
                stringNodeEmpty.Value.Value = textexp.Empty;
                stringNodeClosed.TrySetRootSource(textexp.Closed);
                slotRef.TrySetRootSource(____rootSlot.Target);
                childCountNode.Instance.Target = slotRef;
                equalsNode.A.Target = childCountNode;
                conditionalNode.Condition.Target = equalsNode;
                conditionalNode.OnTrue.Target = stringNodeEmpty;
                conditionalNode.OnFalse.Target = stringNodeClosed;
                driverNode.TrySetRootTarget(textexp.Empty);
                driverNode.Value.Target = conditionalNode;
            }
        }

        // maybe what ui has been generated under an editor should be cashed
        [HarmonyPatch(typeof(BagEditor), "OnChanges")]
        class BagEditor_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => Editor_OnChanges_Transpiler(codes, AccessTools.Field(typeof(BagEditor), "_targetBag"));

            [HarmonyPrefix, HarmonyPatch("Target_ElementAdded")]
            static bool AddedPrefix(BagEditor __instance, SyncField<RefID> ____targetBag, IWorldElement element)
            {
                if (!ShouldBuild(____targetBag)) return false;
                foreach (var child in __instance.Slot.Children)
                {
                    var comp = child.GetComponent<BagEditorItem>();
                    if (comp != null && comp.Item.Target == element)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(ListEditor), "OnChanges")]
        class ListEditor_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => Editor_OnChanges_Transpiler(codes, AccessTools.Field(typeof(ListEditor), "_targetList"));

            // maybe a better strategy here is to transpile Target_ElementsAdded to not create a slot,
            // then just prefix BuildListItem
            [HarmonyPrefix, HarmonyPatch("Target_ElementsAdded")]
            private static bool AddedPrefix(ListEditor __instance, SyncField<RefID> ____targetList, ISyncList list, int startIndex, int count)
            {
                return ShouldBuild(____targetList);
            }
        }

        [HarmonyPatch(typeof(UserInspectorItem))]
        class UserInspectorItem_Patch
        {
            [HarmonyTranspiler]
            [HarmonyPatch("OnChanges")]
            static IEnumerable<CodeInstruction> OnChangesTranspiler(IEnumerable<CodeInstruction> codes) => Editor_OnChanges_Transpiler(codes, AccessTools.Field(typeof(UserInspectorItem), "_user"));

            //the cleaner solution is creating a PatchedSync<T> but Sync<T> is sealed and net 9 enforces that at runtime
            [HarmonyPrefix]
            [HarmonyPatch("OpenComponents")]
            static bool OpenComponentsPrefix(UserInspectorItem __instance, SyncRef<User> ____user)
            {
                if (____user.Target == null || __instance.World.IsAuthority ||
                    __instance.Slot.GetComponentInParents<UserInspector>() is not UserInspector inspector) return true;
                OpenUserInspector(inspector, new(View.Components, ____user.Target));
                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch("OpenStream")]
            static bool OpenStreamPrefix(UserInspectorItem __instance, SyncRef<User> ____user, ushort streamIndex)
            {
                if (____user.Target == null || __instance.World.IsAuthority ||
                    __instance.Slot.GetComponentInParents<UserInspector>() is not UserInspector inspector) return true;
                OpenUserInspector(inspector, new(View.Streams, ____user.Target, streamIndex));
                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch("OpenUser")]
            static bool OpenUserPrefix(UserInspectorItem __instance, SyncRef<User> ____user)
            {
                if (____user.Target == null || __instance.World.IsAuthority ||
                    __instance.Slot.GetComponentInParents<UserInspector>() is not UserInspector inspector) return true;
                OpenUserInspector(inspector, new(View.User, ____user.Target));
                return false;
            }
        }
        static MethodInfo _FilterWorker = AccessTools.Method(typeof(UserInspector), "FilterWorker");
        static void OpenUserInspector(UserInspector Inspector, UserInspectorState state)
        {
            userInspectorStates.AddOrUpdate(Inspector, state);
            ((SyncRef<Sync<string>>)Inspector.GetSyncMember("_userText")).Target?.Value = $"User: {state.Target?.UserName} ({state.Target?.ReferenceID})";
            var uislot = ((SyncRef<Slot>)Inspector.GetSyncMember("_workersContentRoot")).Target;
            if (uislot == null) return;
            uislot.DestroyChildren();
            uislot.AddSlot("WorkersRoot").AttachComponent<WorkerInspector>().SetupContainer(state.Target, null, _FilterWorker.CreateDelegate<System.Predicate<Worker>>(Inspector), state.View == View.User);
        }

        [HarmonyPatch(typeof(UserInspector), "FilterWorker")]
        class UserInspector_Patch
        {
            static bool Prefix(UserInspector __instance, ref bool __result, Worker worker)
            {
                UserInspectorState state;
                if (!userInspectorStates.TryGetValue(__instance, out state)) return true;

                switch (state.View)
                {
                    case View.Components:
                        __result = worker is UserComponent;
                        break;
                    case View.Streams:
                        if (worker is Stream stream) __result = stream.GroupIndex == state.StreamGroup;
                        else __result = false;
                        break;
                    case View.User:
                        __result = worker is User;
                        break;
                    default:
                        __result = true;
                        break;
                }
                return false;
            }
        }

        static FieldInfo _typeToIndex = AccessTools.Field(typeof(AssemblyTypeRegistry), "_typeToIndex");
        [HarmonyPatch(typeof(TypeManager), "InitializeAssemblies")]
        class TypeManager_Patch
        {
            static void Postfix(Dictionary<Assembly, int> ____assemblyToIndex, List<AssemblyTypeRegistry> ____allowedAssemblies)
            {
                int index;
                if (____assemblyToIndex.TryGetValue(typeof(SyncRef<>).Assembly, out index))
                {
                    ____assemblyToIndex.TryAdd(typeof(PatchedSyncRef<>).Assembly, index);
                    var tTI = (Dictionary<Type, int>)_typeToIndex.GetValue(____allowedAssemblies[index]);
                    tTI.TryAdd(typeof(PatchedSyncRef<>), tTI[typeof(SyncRef<>)]);
                }
            }
        }

        [HarmonyPatch(typeof(SyncType), "set_Value")]
        class SyncType_Patch
        {
            static Exception Finalizer(Exception __exception, SyncType __instance, Type value)
            {
                if (__exception?.Message.Contains("MyInspectors") == true)
                {
                    SetBase(__instance, value);
                    return null;
                }
                return __exception;
            }

            [HarmonyReversePatch]
            [HarmonyPatch(typeof(SyncField<Type>), "set_Value")]
            public static void SetBase(object instance, Type value) { }
        }

        [HarmonyPatch(typeof(Coder), "IsEnginePrimitive")]
        class Coder_Patch
        {
            static bool Prefix(ref bool __result, Type type)
            {
                if(type?.Name?.Contains("MyInspectors") == true) { __result = true; return false; }
                return true;
            }
        }
        
    }
}