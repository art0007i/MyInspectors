using Elements.Core;
using FrooxEngine;
using System.IO;
using static MyInspectors.MyInspectors;

namespace MyInspectors;

class PatchedSyncRef<T> : SyncRef<T>, EditorTargetField where T : class, IWorldElement
{
    RefID? remoteValue;
    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage) => Decode(reader, inboundMessage);
    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage) => Decode(reader, inboundMessage);
    void Decode(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        RefID value = reader.Read7BitEncoded();
        remoteValue = value;
        InternalSetValue(in value, sync: false);
    }
    protected override bool InternalSetRefID(in RefID id, T prevTarget)
    {
        if (!config.GetValue(KEY_ENABLE)) return base.InternalSetRefID(id, prevTarget);

        RefID value = id;
        bool sync = false;
        bool change = true;

        if (remoteValue.HasValue)
        {
            var editType = Parent.GetType();
            if (typeof(SlotInspector).IsAssignableFrom(editType) || typeof(UserInspectorItem).IsAssignableFrom(editType))
            {
                if (remoteValue == RefID.Null) return base.InternalSetValue(in value, sync, change);
                
                // sync null
                sync = true;
                var current = value;
                value = RefID.Null;
                remoteValue = value;
                change = false;
                World.RunInUpdates(1, () =>
                {
                    _value = current;
                    ValueChanged();
                });
            }
            else
            {
                sync = true; // changing it wont matter since its already setup remotely
            }
        }
        return base.InternalSetValue(in value, sync, change);
    }

    bool EditorTargetField.ShouldBuild => !remoteValue.HasValue || (remoteValue.HasValue && IsNullOrDisposed(remoteValue.Value, World));

    static bool IsNullOrDisposed(RefID id, World world)
    {
        if (id == RefID.Null) return true;
        var element = world.ReferenceController.GetObjectOrNull(id);

        return element == null || element.IsRemoved;
    }
}

interface EditorTargetField
{
    bool ShouldBuild { get; }
}