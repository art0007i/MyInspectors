using HarmonyLib;
using NeosModLoader;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using FrooxEngine;
using FrooxEngine.LogiX;
using BaseX;
using System.Reflection.Emit;
using FrooxEngine.UIX;

namespace MyInspectors
{
    public class MyInspectors : NeosMod
    {
        public override string Name => "MyInspectors";
        public override string Author => "art0007i";
        public override string Version => "1.2.0";
        public override string Link => "https://github.com/art0007i/MyInspectors/";
        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.art0007i.MyInspectors");
            //harmony.PatchAll();
            foreach (var type in @switch.Keys)
            {
                harmony.Patch(AccessTools.Method(type, "OnChanges"), transpiler: new HarmonyMethod(typeof(MyInspectors).GetMethod("Delocalizer")));
                var removeMethod = AccessTools.Method(type, "RemovePressed");
                if (removeMethod != null) harmony.Patch(removeMethod, transpiler: new HarmonyMethod(typeof(MyInspectors).GetMethod("Fixup")));
                var addNewMethod = AccessTools.Method(type, "AddNewPressed");
                if (addNewMethod != null) harmony.Patch(addNewMethod, transpiler: new HarmonyMethod(typeof(MyInspectors).GetMethod("Fixup")));
            }
        }
        // the patches in this mod are very similar to each other...
        // there might be a better solution to solving this problem (Transpiler)
        // all that would need to change for the originals is replace the check for host to a check for if the local user has caused the change
        // though my current method of detecting this (SyncRef.IsSyncDirty) doesn't always work (see -> SceneInspector_Patch.AttachPatch)

        // but in theory if that is solved, replacing the IsAuthority check to a 'value changed locally' check in all OnChanges methods on a few classes should do the same thing as this mod currently
        // the classes for normal inspectors: WorkerInspector, SceneInspector, SlotInspector, ListEditor, BagEditor

        // the classes for user inspectors:   UserInspector,  UserInspectorItem


        // a few components related to inspector generation exhibit this pattern:
        // 1. a "targetField" is set by a user
        // 2. the host notices this change, and updates "privateField" to match "targetField" and registers some events

        // this mod makes step 2. execute on the user, and then changes "targetField" to null, to prevent the host from registering events as well
        public struct DataContainer
        {
            public FieldInfo targetField;
            public FieldInfo privateField;

            public DataContainer(Type type, string targetField, string privateField)
            {
                this.targetField = AccessTools.Field(type, targetField);
                this.privateField = AccessTools.Field(type, privateField);
            }
        }

        // the SceneInspector type is special, and it has special handling in the code later.
        // I'm just misusing my own struct because I can :P
        public static Dictionary<Type, DataContainer> @switch = new() {
            { typeof(WorkerInspector), new(typeof(WorkerInspector), "_targetContainer", "_currentContainer") },
            { typeof(SceneInspector), new(typeof(SceneInspector), "ComponentView", "Root") },
            //{ typeof(SlotInspector), new(typeof(SlotInspector), "_rootSlot", "_setupRoot") },
            //{ typeof(ListEditor), new(typeof(ListEditor), "_targetList", "_registeredList") },
            //{ typeof(BagEditor), new(typeof(BagEditor), "_targetBag", "_registeredBag") },
        };

        // the transpiler is cool but it doesnt work
        // it makes sense in my head
        
        public static IEnumerable<CodeInstruction> Delocalizer(IEnumerable<CodeInstruction> instructions, MethodBase func)
        {
            var selfType = func.DeclaringType;
            var selfData = @switch[selfType];

            var codes = instructions.ToList();


            Debug("ORIGG VOCDE!-======================----------- " + selfType);
            for (var i = 0; i < codes.Count; i++)
            {
                Debug("IL_" + i.ToString("X4") + " " + codes[i].ToString());
            }
            Debug("\n\n\n\n"); 

            for (var i = 0; i < codes.Count; i++)
            {
                var code = codes[i];
                // we need to replace the IsAuthority check with our own 'local value changed' check
                // this is basically a check that tells if a value has been changed on the client, but not networked yet
                if (code.Is(OpCodes.Callvirt, typeof(World).GetMethod("get_IsAuthority")))
                {
                    // find where the IsAuthority check skips to when it fails
                    // we need this later when we want to clear the 'targetField'
                    int endindex = 0;
                    if (codes[i+1].Branches(out Label? l))
                    {
                        endindex = codes.FindIndex((c) => c.labels.Contains((Label)l));
                    }
                    else
                    {
                        Error("Error while patching method 'OnChanges' on type " + selfType);
                        throw new Exception("Error while patching method: " + func);
                    }

                    // replace existing instructions with ones that work for us

                    // the following block basically evaluates to
                    // `| this.'targetField'.IsSyncDirty | this.Parent.componentBag.IsSyncDirty`
                    // this checks whether the component bag has been locally modified
                    // aka our component has just been attached
                    var insert = new List<CodeInstruction>
                    {
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldfld, selfData.targetField),
                        new(OpCodes.Call, typeof(SyncElement).GetMethod("get_IsSyncDirty")),
                        new(OpCodes.Or),
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Call, typeof(IWorldElement).GetMethod("get_Parent")),
                        new(OpCodes.Ldfld, AccessTools.Field(typeof(ContainerWorker<Component>), "componentBag")),
                        new(OpCodes.Call, typeof(SyncElement).GetMethod("get_IsSyncDirty")),
                        // labels in il code are difficult
                        // screw lazy evaluation, i'm doing this instead
                        new(OpCodes.Or),
                    };
                    if(selfType != typeof(SceneInspector))
                    {
                        // the following block basically evaluates to
                        // `this.'targetField'.Target = null;`
                        // this is what sets the 'targetField' to null so the host doesn't recognize that it changed
                        var endInsert = new CodeInstruction[]
                        {
                            new(OpCodes.Ldarg_0),
                            new(OpCodes.Ldfld, selfData.targetField),
                            new(OpCodes.Ldnull),
                            new(OpCodes.Callvirt, selfData.targetField.FieldType.GetMethod("set_Target")),
                        };
                        // insert the codes before the end of the if block
                        codes.InsertRange(codes.Count - 2, endInsert);
                    }
                    if (selfType == typeof(SceneInspector))
                    {
                        // the following block basically evaluates to
                        // `| this.'privateField'.IsSyncDirty`
                        var insert2 = new CodeInstruction[]
                        {
                            new(OpCodes.Ldarg_0),
                            new(OpCodes.Ldfld, selfData.privateField),
                            new(OpCodes.Call, typeof(SyncElement).GetMethod("get_IsSyncDirty")),
                            new(OpCodes.Or),
                        };
                        insert.AddRange(insert2);
                    }

                    codes.InsertRange(i+1, insert);
                    break;
                }
            }

            // prints il instructions
            // useful for debugging
            Debug("PAQTHC FOR METHOD!!!!:L::!::!: " + selfType);
            for (var i = 0; i < codes.Count; i++)
            {
                Debug("IL_" + i.ToString("X4") + " " + codes[i].ToString());
            }
            Debug("\n\n\n\n");

            return codes.AsEnumerable();
        }
        

        // since we change the 'targetField' to null anything referencing it will run into a null reference exception
        // but the 'privateField' contains a working reference to the field that we can use
        public static IEnumerable<CodeInstruction> Fixup(IEnumerable<CodeInstruction> codes, MethodBase func)
        {
            var selfData = @switch[func.DeclaringType];

            bool deleteNext = false;
            foreach (var code in codes)
            {
                if (deleteNext)
                {
                    deleteNext = false;
                    continue;
                }
                if (code.Is(OpCodes.Ldfld, selfData.targetField))
                {
                    code.operand = selfData.privateField;
                    deleteNext = true;
                }
                yield return code;
            }
        }

        /*
        [HarmonyPatch(typeof(SceneInspector))]
        class SceneInspector_Patch
        {
            [HarmonyPatch("OnAttach")]
            [HarmonyPostfix]
            public static void AttachPatch(SceneInspector __instance)
            {
                // run in updates 0 to wait until the target has been updated
                __instance.RunInUpdates(0, () => HookMethod(__instance.ComponentView, true));
            }

            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static void ChangesPatch(SceneInspector __instance)
            {
                HookMethod(__instance.ComponentView);
            }

            public static void HookMethod(SyncRef<Slot> compView, bool force = false)
            {
                var based = compView.Parent as SceneInspector;
                var curCom = AccessTools.Field(typeof(SceneInspector), "_currentComponent").GetValue(based) as SyncRef<Slot>;
                // "IsSyncDirty" basically means that it's state has been modified but hasn't been sent over the network yet
                if (based.ComponentView.IsSyncDirty || based.Parent.QuickGetField<WorkerBag<Component>>("componentBag").IsSyncDirty)
                {
                    // This is basically the default "SceneInspector.OnChanges" function.
                    if (curCom.Target != based.ComponentView.Target)
                    {
                        if (curCom.Target != null)
                        {
                            curCom.Target.RemoveGizmo(null);
                        }
                        if (based.ComponentView.Target != null && !based.ComponentView.Target.IsRootSlot)
                        {
                            based.ComponentView.Target.GetGizmo(null);
                        }
                        var comConRoot = AccessTools.Field(typeof(SceneInspector), "_componentsContentRoot").GetValue(based) as SyncRef<Slot>;
                        comConRoot.Target.DestroyChildren(false, true, false, null);
                        curCom.Target = based.ComponentView.Target;
                        var comText = AccessTools.Field(typeof(SceneInspector), "_componentText").GetValue(based) as SyncRef<Sync<string>>;
                        SyncField<string> target4 = comText.Target;
                        string str2 = "Słot: ";
                        Slot target5 = curCom.Target;
                        target4.Value = str2 + (((target5 != null) ? target5.Name : null) ?? "<i>null</i>");
                        if (curCom.Target != null)
                        {
                            comConRoot.Target.AddSlot("ComponentRoot", true).AttachComponent<WorkerInspector>().SetupContainer(curCom.Target);
                        }
                    }
                }
            }
        }
        // This part is for making newly added/removed components processed by local user instead of host
        [HarmonyPatch(typeof(WorkerInspector))]
        class WorkerInspector_Patch
        {
            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static bool Prefix(SyncRef<Worker> ____targetContainer)
            {
                WorkerHook(____targetContainer);

                // cancel normal method, its functionality is fully replaced by WorkerHook.
                return false;
            }

            [HarmonyPatch("SetupContainer")]
            [HarmonyPostfix]
            public static void Postfix(SyncRef<Worker> ____targetContainer)
            {
                WorkerHook(____targetContainer, true);
            }

            public static void WorkerHook(SyncRef<Worker> w, bool force = false)
            {
                WorkerInspector __instance = w.Parent as WorkerInspector;
                var curConField = AccessTools.Field(typeof(WorkerInspector), "_currentContainer");
                var tarCon = AccessTools.Field(typeof(WorkerInspector), "_targetContainer").GetValue(__instance) as SyncRef<Worker>;
                if (tarCon.Target != null && tarCon.IsSyncDirty || force) //__instance.Parent.QuickGetField<WorkerBag<Component>>("componentBag").IsSyncDirty
                {
                    if (curConField.GetValue(__instance) != tarCon.Target)
                    {
                        AccessTools.Method(__instance.GetType(), "UnregisterEvents").Invoke(__instance, null);
                        Slot slot = tarCon.Target as Slot;
                        User user = tarCon.Target as User;
                        var comAdd = AccessTools.Method(__instance.GetType(), "OnComponentAdded").CreateDelegate(typeof(ComponentEvent<Component>), __instance) as ComponentEvent<Component>;
                        var comRem = AccessTools.Method(__instance.GetType(), "OnComponentRemoved").CreateDelegate(typeof(ComponentEvent<Component>), __instance) as ComponentEvent<Component>;
                        var usrComAdd = AccessTools.Method(__instance.GetType(), "UserComponentAdded").CreateDelegate(typeof(ComponentEvent<UserComponent>), __instance) as ComponentEvent<UserComponent>;
                        var usrComRem = AccessTools.Method(__instance.GetType(), "UserComponentRemoved").CreateDelegate(typeof(ComponentEvent<UserComponent>), __instance) as ComponentEvent<UserComponent>;
                        var streamAdd = AccessTools.Method(__instance.GetType(), "StreamAdded").CreateDelegate(typeof(Action<Stream>), __instance) as Action<Stream>;
                        var streamRem = AccessTools.Method(__instance.GetType(), "StreamRemoved").CreateDelegate(typeof(Action<Stream>), __instance) as Action<Stream>;

                        if (slot != null)
                        {
                            slot.ComponentAdded += comAdd;
                            slot.ComponentRemoved += comRem;
                        }
                        if (user != null)
                        {
                            user.ComponentAdded += usrComAdd;
                            user.ComponentRemoved += usrComRem;
                            user.StreamAdded += streamAdd;
                            user.StreamRemoved += streamRem;
                        }
                        curConField.SetValue(__instance, tarCon.Target);
                    }
                    tarCon.Target = null;
                }
            }
        }

        [HarmonyPatch(typeof(ListEditor))]
        class ListEditorPatch
        {

            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static bool Prefix(ListEditor __instance)
            {
                EditorHook(__instance);

                // cancel normal method, its functionality is fully replaced by WorkerHook.
                return false;
            }
            
            [HarmonyPatch("Setup")]
            [HarmonyPostfix]
            public static void Postfix(ListEditor __instance)
            {
                EditorHook(__instance, true);
            }
            
            public static void EditorHook(ListEditor hook, bool force = false)
            {
                var tgtList = hook.QuickGetField<SyncRef<ISyncList>>("_targetList");

                if (tgtList.IsSyncDirty || force && tgtList.Target != null && !hook.QuickGetField<bool>("setup"))
                {
                    hook.QuickSetField<bool>("setup", true);
                    hook.QuickSetField<ISyncList>("_registeredList", tgtList.Target);
                    hook.Slot.DestroyChildren(false, true, false, null);
                    tgtList.Target.ElementsAdded += hook.QuickDelegate<SyncListElementsEvent>("Target_ElementsAdded");
                    tgtList.Target.ElementsRemoved += hook.QuickDelegate<SyncListElementsEvent>("Target_ElementsRemoved");
                    tgtList.Target.ListCleared += hook.QuickDelegate<SyncListEvent>("Target_ListCleared");
                    hook.QuickCall("Target_ElementsAdded", new object[] { tgtList.Target, 0, tgtList.Target.Count });

                    tgtList.Target = null;
                }
                if (hook.QuickGetField<bool>("reindex"))
                {
                    hook.QuickCall("Reindex");
                    hook.QuickSetField<bool>("reindex", false);
                }
            }
        }

        [HarmonyPatch(typeof(BagEditor))]
        class BagEditorPatch
        {

            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static bool Prefix(BagEditor __instance)
            {
                EditorHook(__instance);
                // cancel normal method, its functionality is fully replaced by WorkerHook.
                return false;
            }
            
            [HarmonyPatch("Setup")]
            [HarmonyPostfix]
            public static void Postfix(BagEditor __instance)
            {
                EditorHook(__instance, true);
            }
            
            public static void EditorHook(BagEditor hook, bool force = false)
            {
                var tgtBag = hook.QuickGetField<SyncRef<ISyncBag>>("_targetBag");

                if (tgtBag.IsSyncDirty || force && tgtBag.Target != null && !hook.QuickGetField<bool>("setup"))
                {
                    hook.QuickSetField<bool>("setup", true);
                    hook.QuickSetField("_registeredBag", tgtBag.Target);
                    hook.Slot.DestroyChildren(false, true, false, null);
                    tgtBag.Target.ElementAdded += hook.QuickDelegate<SyncBagElementEvent>("Target_ElementAdded");
                    tgtBag.Target.ElementRemoved += hook.QuickDelegate<SyncBagElementEvent>("Target_ElementRemoved");
                    foreach (KeyValuePair<object, IWorldElement> keyValuePair in tgtBag.Target.Elements)
                    {
                        hook.QuickCall("Target_ElementAdded", new object[] { tgtBag.Target, keyValuePair.Key, keyValuePair.Value });
                    }
                    hook.QuickGetField<SyncRef<Button>>("_addNewButton").Target.Enabled = tgtBag.Target.CanAutoAddElements;

                    tgtBag.Target = null;
                }
            }
        }
        */
        
    }
}