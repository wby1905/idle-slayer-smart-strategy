using UnityEngine;

namespace SmartStrategy;

// The only IL2CPP-injected strategy type. Keep its instance-method surface limited to
// parameterless Unity messages; managed strategy types and compiler-generated closures must
// never enter ClassInjector.ConvertMethodInfo. Calls from patches/UI remain ordinary C# calls.
public sealed class StrategyHost : MonoBehaviour
{
    private StrategyBehaviour _strategy;

    public void Awake()
    {
        if (StrategyBehaviour.Current != null)
        {
            UnityEngine.Object.Destroy(this);
            return;
        }
        try
        {
            gameObject.name = nameof(StrategyBehaviour);
            _strategy = new StrategyBehaviour();
            _strategy.Awake();
            if (!ReferenceEquals(StrategyBehaviour.Current, _strategy))
                UnityEngine.Object.Destroy(this);
        }
        catch (Exception ex)
        {
            SmartLog.Error(SmartLogCategory.Runtime, $"Strategy controller initialization failed: {ex}");
            try { _strategy?.OnDestroy(); }
            catch (Exception cleanup) { SmartLog.Warning(SmartLogCategory.Runtime, $"Initialization cleanup failed: {cleanup.Message}"); }
            _strategy = null;
            UnityEngine.Object.Destroy(this);
        }
    }

    public void Update() => _strategy?.Update();

    public void OnDestroy()
    {
        try { _strategy?.OnDestroy(); }
        finally { _strategy = null; }
    }

    public void OnApplicationQuit() => _strategy?.OnApplicationQuit();
}
