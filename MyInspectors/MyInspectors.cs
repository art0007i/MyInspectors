using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using FrooxEngine;
using Elements.Core;
using System.Reflection.Emit;
using FrooxEngine.UIX;
using System.Diagnostics;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Slots;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using FrooxEngine.ProtoFlux;
using FrooxEngine.ProtoFlux.Runtimes;
using FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes;

namespace MyInspectors
{
    public class MyInspectors : ResoniteMod
    {
        public override string Name => "MyInspectors";
        public override string Author => "art0007i"; // with massive help from https://github.com/EIA485
        public override string Version => "2.0.0";
        public override string Link => "https://github.com/art0007i/MyInspectors/";
        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.art0007i.MyInspectors");
            harmony.PatchAll();
        }
        static FieldInfo _targetContainer = AccessTools.Field(typeof(WorkerInspector), "_targetContainer");
        static FieldInfo _value = AccessTools.Field(typeof(SyncField<RefID>), "_value");
        static MethodInfo ValueChanged = AccessTools.Method(typeof(SyncField<RefID>), "ValueChanged");
        static Dictionary<SyncField<RefID>, RefID> RemoteValues = new();

        // patching 'hot' code. but like idk how else to do it
        [HarmonyPatch(typeof(ComponentBase<Component>), "OnAwake")]
        class StupidInspectorFixupPatch
        {
            public static void Postfix(object __instance)
            {
                if (__instance is SceneInspector i)
                {
                    // onchanges triggers 1 update later, when we need 0 updates so it doesn't sync
                    i.RunInUpdates(0, () =>
                    {
                        Traverse.Create(__instance).Method("OnChanges").GetValue();
                    });
                }
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
            public static bool IsChangedLocally(ConflictingSyncElement el)
            {
                return el.LastConfirmedTime == el.LastHostVersion || el.IsSyncDirty;
            }

            public static bool ShouldContinue(Worker worker)
            {
                if (worker is SceneInspector s)
                {
                    return IsChangedLocally(s.ComponentView) || IsChangedLocally(s.Root);
                }
                else if (worker is WorkerInspector w)
                {
                    // doing this is weird, because we won't unregister events whenever you set a workerinspector to null.
                    // but also this never happens by default so hope it's ok
                    var target = _targetContainer.GetValue(w) as SyncRef<Worker>;
                    return target.Target != null && IsChangedLocally(target);
                }
                return false;
            }

            public static void ResetContainer(WorkerInspector i)
            {
                if (!i.World.IsAuthority)
                    (_targetContainer.GetValue(i) as SyncRef<Worker>).Target = null;
            }
            public static bool IsHost(SceneInspector i)
            {
                return i.World.IsAuthority;
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo _currentContainer = AccessTools.Field(typeof(WorkerInspector), "_currentContainer");

                var codes = instructions.ToList();
                var rootfield = typeof(SceneInspector).GetField("Root");
                var compfield = typeof(SceneInspector).GetField("ComponentView");
                for (var i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    if (code.opcode == OpCodes.Beq)
                    {
                        var code2 = codes[i - 2];
                        // targets the "if currentRoot != targetRoot" statements in scene inspector
                        if (code2.operand is FieldInfo f && (f == rootfield || f == compfield))
                        {
                            // if (IsHost | IsChangedLocally) & !(original statement)
                            yield return new CodeInstruction(OpCodes.Ceq);
                            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                            yield return new CodeInstruction(OpCodes.Ceq);
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Call, typeof(SceneInspector_Patch).GetMethod(nameof(IsHost)));
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldfld, code2.operand);
                            yield return new CodeInstruction(OpCodes.Callvirt, typeof(SceneInspector_Patch).GetMethod(nameof(IsChangedLocally)));
                            yield return new CodeInstruction(OpCodes.Or);
                            yield return new CodeInstruction(OpCodes.And);
                            code.opcode = OpCodes.Brfalse;
                        }
                    }
                    yield return code;
                    if (code.Calls(typeof(World).GetMethod("get_IsAuthority")))
                    {
                        // is host OR the value has been changed by local user
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Callvirt, typeof(SceneInspector_Patch).GetMethod(nameof(ShouldContinue)));
                        yield return new CodeInstruction(OpCodes.Or);
                    }
                    if (code.StoresField(_currentContainer))
                    {
                        // reset the container so others don't know about it
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, typeof(SceneInspector_Patch).GetMethod(nameof(ResetContainer)));
                    }
                }
            }
        }

        [HarmonyPatch(typeof(SyncField<RefID>), "InternalSetValue")]
        class SyncField_Patch
        {
            static internal bool Prefix(SyncField<RefID> __instance, ref RefID value, ref bool sync, ref bool change)
            {
                if (__instance == null || __instance.World.IsAuthority) return true;

                var parent = __instance.Parent;
                var editType = EditorType(parent.GetType());
                switch (editType)
                {
                    case EditType.slot:
                        if (!(__instance is SyncRef<Slot>) || ((SlotInspector)parent).GetSyncMember("_rootSlot") != __instance)
                            return true;
                        break;
                    case EditType.user:
                        if (!(__instance is SyncRef<User>) || ((UserInspectorItem)parent).GetSyncMember("_user") != __instance)
                            return true;
                        break;
                    case EditType.bag:
                        if (!(__instance is SyncRef<ISyncBag>) || ((BagEditor)parent).GetSyncMember("_targetBag") != __instance)
                            return true;
                        break;
                    case EditType.list:
                        if (!(__instance is SyncRef<ISyncList>) || ((ListEditor)parent).GetSyncMember("_targetList") != __instance)
                            return true;
                        break;
                    default:
                        return true;
                }
                sync = false;

                //maybe i should be using the IsChangedLocally function somehow to determine if its a local change or not but this is just what came first to mind and works for now.
                //index 2 because the first one is our patch
                var caller = new StackTrace().GetFrame(2).GetMethod();
                //comparing by name instead of ref since im not sure how this will work with a non generic method in a generic class. may be worth testing later.
                if (caller.Name == "InternalDecodeDelta" || caller.Name == "InternalDecodeFull")
                {
                    if (!RemoteValues.ContainsKey(__instance))
                    {
                        ((Worker)__instance.Parent).Disposing += (worker) => RemoteValues.Remove(__instance);//guess i could restructure this to not create runtime delegates. in theory it would use less memory but would probably also be very slightly slower
                        RemoteValues[__instance] = value;
                    }
                    else if ((editType == EditType.bag || editType == EditType.list) && RemoteValues[__instance] == RefID.Null) RemoteValues[__instance] = value;//even if the sync is null itl still act like what ever the first non null value it had even if that first value no longer exists 
                    else RemoteValues[__instance] = value;
                }
                else if (RemoteValues.ContainsKey(__instance))
                {
                    if (editType == EditType.slot || editType == EditType.user)
                    {
                        if (RemoteValues[__instance] != RefID.Null)
                        {
                            sync = true;
                            var curval = value;
                            value = RefID.Null; //sync null
                            RemoteValues[__instance] = value;
                            change = false;
                            __instance.World.RunInUpdates(1, () => { _value.SetValue(__instance, curval); ValueChanged.Invoke(__instance, null); });
                        }
                    }
                    else
                    {
                        sync = true; //changing it wont matter since its already setup remotely
                    }
                }

                return true;
            }
        }

        //doing this to support inherited classes
        static EditType EditorType(Type type)
        {
            if (typeof(SlotInspector).IsAssignableFrom(type)) return EditType.slot;
            else if (typeof(UserInspectorItem).IsAssignableFrom(type)) return EditType.user;
            else if (typeof(BagEditor).IsAssignableFrom(type)) return EditType.bag;
            else if (typeof(ListEditor).IsAssignableFrom(type)) return EditType.list;
            return (EditType)(-1);
        }
        enum EditType
        {
            slot,
            user,
            bag,
            list
        }
        static bool IsNullOrDisposed(RefID id, World world)
        {
            if (id == RefID.Null) return true;
            var element = world.ReferenceController.GetObjectOrNull(id);
            if (element == null || element.IsRemoved) return true;
            return false;
        }
        static bool ShouldBuild(SyncField<RefID> field)
        {
            if (field.World.IsAuthority) return true; //not explicitly needed but this way we dont need to search thru the RemoteValues dict
            if (RemoteValues.ContainsKey(field) && !IsNullOrDisposed(RemoteValues[field], field.World)) return false;
            return true;
        }
        static MethodInfo ShouldBuildInfo = AccessTools.Method(typeof(MyInspectors), nameof(ShouldBuild));
        static IEnumerable<CodeInstruction> Editor_OnChanges_Transpiler(IEnumerable<CodeInstruction> codes, FieldInfo targetField)
        {
            int hit = 0;
            foreach (var code in codes)
            {
                if (hit <= 0 && (code.operand as MethodInfo)?.Name == "get_World")
                {
                    hit++;
                    yield return new(OpCodes.Ldfld, targetField);
                    yield return new(OpCodes.Call, ShouldBuildInfo);
                }
                else if (hit == 1)
                {
                    hit++; //skip an instruction in codes
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
                if (textexp.Empty.IsLinked) return;
                textexp.CustomEmptyCheck.Target = null; //use default empty check
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
        [HarmonyPatch(typeof(UserInspectorItem), "OnChanges")]
        class UserInspectorItem_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => Editor_OnChanges_Transpiler(codes, AccessTools.Field(typeof(UserInspectorItem), "_user"));
        }
        //maybe what ui has been generated under an editor should be cashed
        [HarmonyPatch(typeof(BagEditor), "OnChanges")]
        class BagEditor_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => Editor_OnChanges_Transpiler(codes, AccessTools.Field(typeof(BagEditor), "_targetBag"));

            [HarmonyPrefix]
            [HarmonyPatch("Target_ElementAdded")]
            static bool AddedPrefix(BagEditor __instance, SyncField<RefID> ____targetBag, IWorldElement element)
            {
                if (!ShouldBuild(____targetBag)) return false;
                foreach (var child in __instance.Slot.Children)
                {
                    var comp = child.GetComponent<BagEditorItem>();
                    if (comp != null)
                    {
                        if (comp.Item.Target == element)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

        }
        [HarmonyPatch(typeof(ListEditor), "OnChanges")]
        class ListEditor_Patch
        {
            static MethodInfo BuildListItem = AccessTools.Method(typeof(ListEditor), "BuildListItem");
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => Editor_OnChanges_Transpiler(codes, AccessTools.Field(typeof(ListEditor), "_targetList"));

            [HarmonyPrefix]
            [HarmonyPatch("Target_ElementsAdded")] //maybe a better strat here is to transpile Target_ElementsAdded to not create a slot then just prefix BuildListItem
            static bool AddedPrefix(ListEditor __instance, SyncField<RefID> ____targetList, ISyncList list, int startIndex, int count)
            {
                if (!ShouldBuild(____targetList)) return false;
                if (count == 1) //we can be more efficient and use more original code.
                {
                    var element = list.GetElement(startIndex);
                    foreach (var child in __instance.Slot.Children)
                    {
                        var source = child[0].GetComponent<ReferenceProxySource>();
                        if (source != null)
                        {
                            if (source.Reference.Target == element) return false;
                        }
                    }
                    return true;
                }
                else
                {
                    HashSet<IWorldElement> built = new();
                    foreach (var child in __instance.Slot.Children)
                    {
                        var source = child[0].GetComponent<ReferenceProxySource>();
                        if (source != null)
                        {
                            built.Add(source.Reference.Target);
                        }
                    }

                    __instance.World?.RunSynchronously(delegate
                        {
                            for (int i = startIndex; i < startIndex + count; i++)
                            {
                                var element = list.GetElement(i);
                                if (built.Contains(element)) continue;
                                Slot root = __instance.Slot.InsertSlot(i, "Element");
                                BuildListItem.Invoke(__instance, new object[] { list, i, root });
                            }
                        });
                }

                return false;
            }
        }
    }
}