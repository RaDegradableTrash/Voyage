using System.Collections;
using UnityEngine;

public class StartProcedure : MonoBehaviour
{
    [SerializeField] private CarControl carControl;
    [SerializeField] private float shutdownDelaySeconds = 1f;

    private readonly bool[] batteries = new bool[4];
    private bool leftPumpOn;
    private bool rightPumpOn;
    private bool engineOn;

    public event System.Action OnStateChanged;

    public bool EngineOn => engineOn;

    private void Awake()
    {
        if (carControl == null)
        {
            carControl = FindObjectOfType<CarControl>();
        }

        if (carControl != null)
        {
            carControl.SetEngineOn(engineOn);
        }
    }

    public bool IsBatteryOn(int index)
    {
        if (index < 0 || index >= batteries.Length)
        {
            return false;
        }

        return batteries[index];
    }

    public bool IsLeftPumpOn()
    {
        return leftPumpOn;
    }

    public bool IsRightPumpOn()
    {
        return rightPumpOn;
    }

    public bool HasAnyBatteryOn()
    {
        return HasAnyBatteryInternal();
    }

    public bool HasAnyPumpOn()
    {
        return leftPumpOn || rightPumpOn;
    }

    public bool CanStartEngine()
    {
        return HasAnyBatteryOn() && (leftPumpOn || rightPumpOn) && FuelTank.SharedFuel > 0f;
    }

    public void ToggleBattery(int index)
    {
        if (index < 0 || index >= batteries.Length)
        {
            return;
        }

        batteries[index] = !batteries[index];
        OnStateChanged?.Invoke();
    }

    public void ToggleLeftPump()
    {
        leftPumpOn = !leftPumpOn;
        OnStateChanged?.Invoke();
    }

    public void ToggleRightPump()
    {
        rightPumpOn = !rightPumpOn;
        OnStateChanged?.Invoke();
    }

    public void ToggleEngine()
    {
        if (engineOn)
        {
            StartCoroutine(ShutdownRoutine());
            return;
        }

        if (!CanStartEngine())
        {
            return;
        }

        engineOn = true;
        if (carControl != null)
        {
            carControl.SetEngineOn(true);
        }
        OnStateChanged?.Invoke();
    }

    private IEnumerator ShutdownRoutine()
    {
        if (carControl != null)
        {
            carControl.SetGear(CarControl.GearMode.Park);
        }

        yield return new WaitForSeconds(shutdownDelaySeconds);
        engineOn = false;
        if (carControl != null)
        {
            carControl.SetEngineOn(false);
        }
        OnStateChanged?.Invoke();
    }

    public void ForceShutdownEngine()
    {
        engineOn = false;
        if (carControl != null)
        {
            carControl.SetEngineOn(false);
            carControl.SetGear(CarControl.GearMode.Park);
        }
        OnStateChanged?.Invoke();
    }

// 添加在 StartProcedure 类中，ForceShutdownEngine 方法之后

// ★★★ 检查并自动恢复启动（当油量恢复时）★★★
public void TryAutoRestartEngine()
{
    // 如果引擎未启动，但有油量且启动条件满足
    if (!engineOn && CanStartEngine())
    {
        // 可选：自动重启，或者触发提示让玩家手动启动
        // 这里实现自动重启（如果你希望自动的话）
        engineOn = true;
        if (carControl != null)
        {
            carControl.SetEngineOn(true);
        }
        OnStateChanged?.Invoke();
        Debug.Log("Engine auto-restarted due to fuel added!");
    }
}

    public void ForceStartVehicle()
{
    // 强制打开所有电池
    for (int i = 0; i < batteries.Length; i++)
    {
        batteries[i] = true;
    }
    
    // 强制打开左右油泵
    leftPumpOn = true;
    rightPumpOn = true;
    
    // 强制启动引擎
    engineOn = true;
    
    // 同步到 CarControl
    if (carControl != null)
    {
        carControl.SetEngineOn(true);
        carControl.SetElectricalPower(true);
    }
    
    // 触发状态变更事件
    OnStateChanged?.Invoke();
}

    private bool HasAnyBatteryInternal()
    {
        for (int i = 0; i < batteries.Length; i++)
        {
            if (batteries[i])
            {
                return true;
            }
        }
        return false;
    }

    
}


