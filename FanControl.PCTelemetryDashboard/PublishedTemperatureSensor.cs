using FanControl.Plugins;

namespace FanControl.PCTelemetryDashboard;

internal sealed class PublishedTemperatureSensor(
    string id,
    string name,
    Func<float?> readValue) : IPluginSensor
{
    private readonly object _gate = new();
    private float? _value;

    public string Id { get; } = id;

    public string Name { get; } = name;

    public float? Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    public void Update()
    {
        float? value;
        try
        {
            value = readValue();
        }
        catch
        {
            value = null;
        }

        lock (_gate)
        {
            _value = value;
        }
    }
}
