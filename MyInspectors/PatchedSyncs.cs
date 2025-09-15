using Elements.Core;
using FrooxEngine;

using static MyInspectors.Plugin;

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
        if (!Enable.Value) return base.InternalSetRefID(id, prevTarget);

        RefID value = id;
        bool sync = false;
        bool change = true;

        if (remoteValue.HasValue)
        {
            var editType = Parent.GetType();
            if (typeof(SlotInspector).IsAssignableFrom(editType) || typeof(UserInspectorItem).IsAssignableFrom(editType) || typeof(UserInspector).IsAssignableFrom(editType))
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

}
class PatchedSync<T> : Sync<T>, EditorTargetField //to build you must run Resonite once with the patcher and assembly dumping enabled. you may need to restart your ide
{
    T remoteValue = default;
    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage) => InternalDecodeFull(reader, inboundMessage);
    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        T value = Coder<T>.Decode(reader);
        remoteValue = value;
        InternalSetValue(in value, sync: false);
    }
    protected override bool InternalSetValue(in T value, bool sync = true, bool change = true)
    {
        if (Enable.Value && !Coder<T>.Equals(remoteValue, value))
        {
            sync = false;
            change = true;
        }
        return base.InternalSetValue(in value, sync, change);
    }
    bool EditorTargetField.ShouldBuild => !Coder<T>.Equals(remoteValue, _value);
}

interface EditorTargetField
{
    bool ShouldBuild { get; }
}