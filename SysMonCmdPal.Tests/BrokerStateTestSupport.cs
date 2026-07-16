// Copyright (c) 2026 SysMonCmdPal

using System.Reflection;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal.Tests;

internal sealed class BrokerRuntimeTestScope : IDisposable
{
    private static readonly FieldInfo SnapshotField =
        typeof(BrokerPushReceiver).GetField(
            "_snapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(BrokerPushReceiver).FullName, "_snapshot");

    private readonly BrokerSensorSnapshot _originalSnapshot;

    public BrokerRuntimeTestScope()
    {
        _originalSnapshot = BrokerPushReceiver.Instance.Snapshot;
    }

    public BrokerPushReceiver Receiver => BrokerPushReceiver.Instance;

    public void SetSnapshot(BrokerSensorSnapshot snapshot) =>
        SetSnapshot(Receiver, snapshot);

    public void Dispose() => SetSnapshot(Receiver, _originalSnapshot);

    public static void SetSnapshot(
        BrokerPushReceiver receiver,
        BrokerSensorSnapshot snapshot) =>
        SnapshotField.SetValue(receiver, snapshot);
}

internal sealed class PrivateFieldScope : IDisposable
{
    private readonly List<(object Target, FieldInfo Field, object? Original)> _changes = [];

    public void Set(object target, string fieldName, object? value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);

        _changes.Add((target, field, field.GetValue(target)));
        field.SetValue(target, value);
    }

    public void Dispose()
    {
        for (int i = _changes.Count - 1; i >= 0; i--)
        {
            var change = _changes[i];
            change.Field.SetValue(change.Target, change.Original);
        }
    }
}
