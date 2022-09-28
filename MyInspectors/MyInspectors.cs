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
        public override string Version => "1.1.0";
        public override string Link => "https://github.com/art0007i/MyInspectors/";
        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.art0007i.MyInspectors");
            harmony.PatchAll();
        }
        [HarmonyPatch(typeof(SceneInspector))]

        class SceneInspector_OnChanges_Patch
        {
            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static bool OnChangesPrefix(SceneInspector __instance, SyncRef<Slot> ____currentComponent, SyncRef<Slot> ____componentsContentRoot, SyncRef<Sync<string>> ____componentText)
            {
                var based = __instance;
                var curCom = ____currentComponent;
                if (based.ComponentView.IsSyncDirty)
                {
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
                        var comConRoot = ____componentsContentRoot;
                        comConRoot.Target.DestroyChildren(false, true, false, null);
                        curCom.Target = based.ComponentView.Target;
                        var comText = ____componentText;
                        SyncField<string> target4 = comText.Target;
                        string str2 = "Slót: ";
                        Slot target5 = curCom.Target;
                        target4.Value = str2 + (((target5 != null) ? target5.Name : null) ?? "<i>null</i>");
                        if (curCom.Target != null)
                        {
                            comConRoot.Target.AddSlot("ComponentRoot", true).AttachComponent<WorkerInspector>().SetupContainer(curCom.Target);
                        }
                    }
                }
                return true;
            }
            [HarmonyPatch("OnAttach")]
            [HarmonyPostfix]
            public static void OnAttachPostfix(SceneInspector __instance) => __instance.ComponentView.OnTargetChange += slotCallback;

        }
        private static void slotCallback(SyncRef<Slot> s)
        {
            if (s.Target != null)
            {
                var i = (SceneInspector)s.Parent;
                i.ComponentView.OnTargetChange -= slotCallback;
                Slot old = s.Target;
                Msg(s.Target.Name);
                i.RunInUpdates(0, () => i.ComponentView.Target = null);
                i.RunInUpdates(2, () => i.ComponentView.Target = old);
            }
        }

        [HarmonyPatch(typeof(WorkerInspector), "BuildUIForComponent")]
        class ComponentOrderPatch
        {
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                var injectCodes = new CodeInstruction[] {
                    new(OpCodes.Ldloc_0),
                    new(OpCodes.Callvirt, typeof(UIBuilder).GetMethod("get_Root")),
                    new(OpCodes.Call, typeof(ComponentOrderPatch).GetMethod("ReOrder"))
                };

                var lst = codes.ToList();
                
                lst.InsertRange(lst.FindIndex((instruction) => 
                    instruction.Is(OpCodes.Callvirt, typeof(UIBuilder).GetMethod("VerticalLayout", new Type[] { typeof(float), typeof(float), typeof(Alignment?) }))) + 2,
                    injectCodes
                );

                return lst.AsEnumerable();
            }

            public static void ReOrder(Slot slot)
            {
                if(!slot.World.IsAuthority)
                {
                    slot.OrderOffset = -1;
                }
            }
        }

        /*
         * This doesn't work because the host will always generate the ui for newly added components, not much I can do here as far as I can tell.
         * 
        [HarmonyPatch(typeof(WorkerInspector))]
        [HarmonyPatch("OnChanges")]
        class WorkerInspector_OnAwake_Patch
        {
            public static bool Prefix(WorkerInspector __instance, Worker ____currentContainer, SyncRef<Worker> ____targetContainer)
            {
                Msg("registering component events");
                var curCon = ____currentContainer;
                var tarCon = ____targetContainer;
                if (curCon != tarCon.Target)
                {
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
                }
                return false;
            }
        }
        */
        /*
         * I was trying to do some really weird stuff with registering change events instead of using the normal "OnChanges" method.
         * 
        [HarmonyPatch(typeof(SceneInspector))]
        [HarmonyPatch("OnStart")]
        class SceneInspector_OnStart_Patch
        {
            public static void Prefix(SceneInspector __instance)
            {
                Msg("hooking field events");
                __instance.ComponentView.Changed += ComponentView_Changed;
                __instance.ComponentView.OnReferenceChange += ComponentView_OnReferenceChange;
                __instance.ComponentView.OnObjectAvailable += ComponentView_OnObjectAvailable;
                __instance.ComponentView.OnTargetChange += ComponentView_OnTargetChange;
                __instance.ComponentView.OnValueChange += ComponentView_OnValueChange;
            }

            private static void ComponentView_Changed(IChangeable obj)
            {
                var reference = (SyncRef<Slot>)obj;
                Msg("chngable changed " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));
            }

            private static void ComponentView_OnReferenceChange(SyncRef<Slot> reference)
            {
                Msg("ref chng " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));

            }
            private static void ComponentView_OnObjectAvailable(SyncRef<Slot> reference)
            {
                Msg("obj here " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));
            }
            private static void ComponentView_OnTargetChange(SyncRef<Slot> reference)
            {
                Msg("trgt chng " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));
            }
            private static void ComponentView_OnValueChange(SyncField<RefID> syncField)
            {
                Msg("val chng " + (syncField.LastModifyingUser != null ? syncField.LastModifyingUser.UserName : null));
            }
        }
        */
    }
}