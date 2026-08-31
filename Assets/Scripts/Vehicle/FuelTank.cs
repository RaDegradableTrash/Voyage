using UnityEngine;
using TMPro;

public class FuelTank : MonoBehaviour
{
    [Header("3D Visual Mesh References")]
    [Tooltip("The 3D Object or Quad representing the fuel meter display container.")]
    public GameObject displayObject;
    
    [Tooltip("The MeshRenderer/Renderer of the progress bar Quad.")]
    public Renderer fuelBarFillRenderer;
    
    [Tooltip("3D Text Mesh (TextMeshPro) for displaying percentage directly in the 3D space.")]
    public TextMeshPro fuelText;

    // ── 🌟 核心新增：车内显示屏的 TMP 槽位 ─────────────────────────────────
    [Header("Car Dashboard Display (New)")]
    [Tooltip("将你车辆内部、中控屏或仪表盘上的 TextMeshPro-Text (UI 或 3D) 拖到这里")]
    public TextMeshProUGUI carDashboardFuelText; // 如果你车内屏幕是 Canvas UI，用 TextMeshProUGUI
    [Tooltip("如果车内屏幕是 3D 空间的 TextMeshPro，请把上面留空，把组件拖到这个槽位")]
    public TextMeshPro carDashboardFuelText3D;   // 如果是 3D 物体 TMP，用这个
    // ────────────────────────────────────────────────────────────────────────

    [Header("Fuel Config")]
    public float maxCapacity = 100f;
    public string fuelItemNameFilter = "fuel"; // If matching held item name

    // Static variables to ensure the fuel level is globally unique and shared across both tanks
    private static float _sharedFuel = 100f; // Initial default fuel
    private static float _sharedMaxCapacity = 100f;
    private static System.Collections.Generic.List<FuelTank> _activeTanks = new System.Collections.Generic.List<FuelTank>();

    public static float SharedFuel
    {
        get => _sharedFuel;
        set
        {
            _sharedFuel = Mathf.Clamp(value, 0f, _sharedMaxCapacity);
            NotifyAllTanksToUpdate();
        }
    }

    public float currentFuel
    {
        get => _sharedFuel;
        set
        {
            _sharedFuel = Mathf.Clamp(value, 0f, maxCapacity);
            NotifyAllTanksToUpdate();
        }
    }

    private static void NotifyAllTanksToUpdate()
    {
        for (int i = _activeTanks.Count - 1; i >= 0; i--)
        {
            if (_activeTanks[i] != null)
            {
                _activeTanks[i].UpdateUI();
            }
            else
            {
                _activeTanks.RemoveAt(i);
            }
        }
    }

    [Header("UI Visuals (Neon Holographic Preset)")]
    public Color lowFuelColor = new Color(0.9f, 0.2f, 0.2f, 0.85f);
    public Color mediumFuelColor = new Color(0.9f, 0.6f, 0.1f, 0.85f);
    public Color fullFuelColor = new Color(0.0f, 0.85f, 0.95f, 0.85f);

    [Header("Procedural Wave Settings")]
    public int waveSegments = 35;
    public float waveSpeed = 7f;
    public float waveAmplitude = 0.05f;
    public float waveFrequency = 11f;

    [Tooltip("For rotated quads: LocalX makes the wave animate vertically along the local X axis.")]
    public enum WaveAxis { LocalX, LocalY, LocalZ }
    public WaveAxis fillHeightAxis = WaveAxis.LocalY; // Overridden to default to LocalY since user fixed rotation

    private MeshFilter _fillMeshFilter;
    private Mesh _proceduralMesh;
    private Vector3[] _baseVertices;
    private Vector3[] _animatedVertices;
    private int[] _baseTriangles;
    private Vector2[] _baseUVs;
    private Renderer[] _displayRenderers;
    private float _currentRatio = 0f;
    private float _targetRatio = 0f;

    [Tooltip("How fast the fuel level transitions visually (ratio per second).")]
    public float fillTransitionSpeed = 0.5f;

    private bool _isLookingAt = false;
    private float _currentAlpha = 0f;
    private float _lookAwayTimer = 0f; // 1-second delay buffer when player looks away
    private MaterialPropertyBlock _propBlock;
    private float _lastAppliedDisplayAlpha = -1f;
    private float _lastAppliedDisplayRatio = -1f;
    private int _lastFuelTextPercent = int.MinValue;
    private int _lastDashboardPercent = int.MinValue;
    private float _lastDashboardColorRatio = -1f;
    private float _cachedFuelColorRatio = -1f;
    private Color _cachedFuelColor;
    private Color[] _displayRendererBaseColors;
    private bool[] _displayRendererHasColor;
    private bool[] _displayRendererHasBaseColor;
    private bool _fuelBarHasColor;
    private bool _fuelBarHasBaseColor;

    private void SetupMaterialTransparent(Material mat)
    {
        if (mat == null) return;

        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f); // 1 = Transparent
        }

        if (mat.HasProperty("_Blend"))
        {
            mat.SetFloat("_Blend", 0f); // 0 = Alpha Blending
        }

        if (mat.HasProperty("_Mode"))
        {
            mat.SetFloat("_Mode", 3f); // 3 = Transparent
        }

        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);

        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");

        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private void Awake()
    {
        if (!_activeTanks.Contains(this))
        {
            _activeTanks.Add(this);
        }
        
        _sharedMaxCapacity = maxCapacity;

        lowFuelColor = new Color(0.9f, 0.2f, 0.2f, 0.85f);
        mediumFuelColor = new Color(0.9f, 0.6f, 0.1f, 0.85f);
        fullFuelColor = new Color(0.0f, 0.85f, 0.95f, 0.85f);

        _propBlock = new MaterialPropertyBlock();
        if (fuelBarFillRenderer != null)
        {
            _fillMeshFilter = fuelBarFillRenderer.GetComponent<MeshFilter>();
            if (_fillMeshFilter != null)
            {
                _proceduralMesh = new Mesh();
                _proceduralMesh.name = "ProceduralFuelWaveMesh";
                _fillMeshFilter.mesh = _proceduralMesh;
                BuildBaseWaveMesh();
            }

            if (fuelBarFillRenderer.material != null)
            {
                SetupMaterialTransparent(fuelBarFillRenderer.material);
            }

            fuelBarFillRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            fuelBarFillRenderer.receiveShadows = false;
            CacheFuelBarMaterialProperties();
        }

        if (displayObject != null)
        {
            _displayRenderers = displayObject.GetComponentsInChildren<Renderer>(true);
            foreach (var r in _displayRenderers)
            {
                if (r.material != null)
                {
                    SetupMaterialTransparent(r.material);
                }

                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            CacheDisplayRendererMaterialProperties();
        }
    }

    private void OnDestroy()
    {
        _activeTanks.Remove(this);
    }

    private void Start()
    {
        if (displayObject != null)
        {
            displayObject.SetActive(false);
        }
        UpdateUI();
        _currentRatio = _targetRatio;
        enabled = false;
    }

    private void Update()
    {
        _currentRatio = Mathf.MoveTowards(_currentRatio, _targetRatio, Time.deltaTime * fillTransitionSpeed);

        if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
        {
            float amountToReduce = maxCapacity * 0.05f;
            currentFuel -= amountToReduce;
        }
        if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadEquals))
        {
            float amountToAdd = maxCapacity * 0.05f;
            currentFuel += amountToAdd;
        }

        if (displayObject != null && displayObject.activeSelf && _proceduralMesh != null)
        {
            AnimateWaveMesh();
        }

        bool shouldShow = _isLookingAt;
        if (!_isLookingAt && _lookAwayTimer > 0f)
        {
            _lookAwayTimer -= Time.deltaTime;
            shouldShow = true;
        }

        float targetAlpha = shouldShow ? 1f : 0f;
        
        if (displayObject != null)
        {
            if (shouldShow && !displayObject.activeSelf)
            {
                displayObject.SetActive(true);
                _currentAlpha = 0f;
                displayObject.transform.localScale = Vector3.one * 0.8f;
            }

            _currentAlpha = Mathf.MoveTowards(_currentAlpha, targetAlpha, Time.deltaTime * 8f);
            displayObject.transform.localScale = Vector3.one * Mathf.SmoothStep(0.8f, 1f, _currentAlpha);

            SetDisplayAlpha(_currentAlpha);

            if (!_isLookingAt && Mathf.Approximately(_currentAlpha, 0f))
            {
                displayObject.SetActive(false);
            }
        }

        // ── 🌟 核心新增：确保车内中控屏文本的每一帧动画和色彩变化同步 ───────
        UpdateCarDashboardText();
        // ────────────────────────────────────────────────────────────────────────

        if (IsIdle())
        {
            enabled = false;
        }
    }

    private bool IsIdle()
    {
        bool ratioSettled = Mathf.Abs(_currentRatio - _targetRatio) <= 0.001f;
        bool displayHidden = displayObject == null || !displayObject.activeSelf;
        return ratioSettled && !_isLookingAt && _lookAwayTimer <= 0f && displayHidden;
    }

    private void BuildBaseWaveMesh()
    {
        int numVerts = (waveSegments + 1) * 2;
        Vector3[] vertices = new Vector3[numVerts];
        Vector2[] uvs = new Vector2[numVerts];
        int[] triangles = new int[waveSegments * 6];

        for (int i = 0; i <= waveSegments; i++)
        {
            float t = (float)i / waveSegments;
            float primary = t - 0.5f;

            if (fillHeightAxis == WaveAxis.LocalX)
            {
                vertices[i * 2] = new Vector3(-0.5f, primary, 0f);
                uvs[i * 2] = new Vector2(0f, t);

                vertices[i * 2 + 1] = new Vector3(-0.5f, primary, 0f);
                uvs[i * 2 + 1] = new Vector2(1f, t);
            }
            else if (fillHeightAxis == WaveAxis.LocalZ)
            {
                vertices[i * 2] = new Vector3(primary, 0f, -0.5f);
                uvs[i * 2] = new Vector2(t, 0f);

                vertices[i * 2 + 1] = new Vector3(primary, 0f, -0.5f);
                uvs[i * 2 + 1] = new Vector2(t, 1f);
            }
            else
            {
                vertices[i * 2] = new Vector3(primary, -0.5f, 0f);
                uvs[i * 2] = new Vector2(t, 0f);

                vertices[i * 2 + 1] = new Vector3(primary, -0.5f, 0f);
                uvs[i * 2 + 1] = new Vector2(t, 1f);
            }

            if (i < waveSegments)
            {
                triangles[i * 6] = i * 2;
                triangles[i * 6 + 1] = i * 2 + 1;
                triangles[i * 6 + 2] = (i + 1) * 2;

                triangles[i * 6 + 3] = (i + 1) * 2;
                triangles[i * 6 + 4] = i * 2 + 1;
                triangles[i * 6 + 5] = (i + 1) * 2 + 1;
            }
        }

        _baseVertices = vertices;
        _animatedVertices = new Vector3[numVerts];
        System.Array.Copy(_baseVertices, _animatedVertices, _baseVertices.Length);
        _baseUVs = uvs;
        _baseTriangles = triangles;

        _proceduralMesh.vertices = _baseVertices;
        _proceduralMesh.uv = _baseUVs;
        _proceduralMesh.triangles = _baseTriangles;
        _proceduralMesh.RecalculateBounds();
        _proceduralMesh.RecalculateNormals();
    }

    private void AnimateWaveMesh()
    {
        if (_animatedVertices == null || _animatedVertices.Length != _baseVertices.Length)
        {
            _animatedVertices = new Vector3[_baseVertices.Length];
            System.Array.Copy(_baseVertices, _animatedVertices, _baseVertices.Length);
        }

        float timeVal = Time.time * waveSpeed;

        for (int i = 0; i <= waveSegments; i++)
        {
            float t = (float)i / waveSegments;
            float fillOffset = -0.5f + _currentRatio;

            float wave = 0f;
            if (_currentRatio > 0.01f && _currentRatio < 0.99f)
            {
                wave = Mathf.Sin(t * waveFrequency + timeVal) * waveAmplitude;
            }

            if (fillHeightAxis == WaveAxis.LocalX)
            {
                _animatedVertices[i * 2 + 1].x = fillOffset + wave;
            }
            else if (fillHeightAxis == WaveAxis.LocalZ)
            {
                _animatedVertices[i * 2 + 1].z = fillOffset + wave;
            }
            else
            {
                _animatedVertices[i * 2 + 1].y = fillOffset + wave;
            }
        }

        _proceduralMesh.vertices = _animatedVertices;
        _proceduralMesh.RecalculateBounds();
    }

    private void CacheDisplayRendererMaterialProperties()
    {
        if (_displayRenderers == null)
        {
            _displayRendererBaseColors = null;
            _displayRendererHasColor = null;
            _displayRendererHasBaseColor = null;
            return;
        }

        _displayRendererBaseColors = new Color[_displayRenderers.Length];
        _displayRendererHasColor = new bool[_displayRenderers.Length];
        _displayRendererHasBaseColor = new bool[_displayRenderers.Length];

        for (int i = 0; i < _displayRenderers.Length; i++)
        {
            Renderer renderer = _displayRenderers[i];
            Color baseColor = Color.cyan;
            if (renderer != null)
            {
                Material mat = renderer.sharedMaterial;
                if (mat != null)
                {
                    _displayRendererHasBaseColor[i] = mat.HasProperty("_BaseColor");
                    _displayRendererHasColor[i] = mat.HasProperty("_Color");
                    if (_displayRendererHasBaseColor[i])
                        baseColor = mat.GetColor("_BaseColor");
                    else if (_displayRendererHasColor[i])
                        baseColor = mat.GetColor("_Color");
                }
            }
            _displayRendererBaseColors[i] = baseColor;
        }
    }

    private void CacheFuelBarMaterialProperties()
    {
        _fuelBarHasColor = false;
        _fuelBarHasBaseColor = false;

        if (fuelBarFillRenderer == null)
            return;

        Material mat = fuelBarFillRenderer.sharedMaterial;
        if (mat == null)
            return;

        _fuelBarHasColor = mat.HasProperty("_Color");
        _fuelBarHasBaseColor = mat.HasProperty("_BaseColor");
    }

    private void SetDisplayAlpha(float alpha)
    {
        if (displayObject == null) return;
        if (_propBlock == null)
            _propBlock = new MaterialPropertyBlock();

        bool displayChanged = Mathf.Abs(_lastAppliedDisplayAlpha - alpha) > 0.001f
            || Mathf.Abs(_lastAppliedDisplayRatio - _currentRatio) > 0.001f;
        if (!displayChanged)
        {
            return;
        }

        _lastAppliedDisplayAlpha = alpha;
        _lastAppliedDisplayRatio = _currentRatio;

        if (_displayRenderers == null || _displayRenderers.Length == 0)
        {
            _displayRenderers = displayObject.GetComponentsInChildren<Renderer>(true);
            CacheDisplayRendererMaterialProperties();
        }

        for (int i = 0; i < _displayRenderers.Length; i++)
        {
            Renderer r = _displayRenderers[i];
            if (r == null) continue;
            if (r == fuelBarFillRenderer) continue;

            r.GetPropertyBlock(_propBlock);
            
            Color baseCol = (_displayRendererBaseColors != null && i < _displayRendererBaseColors.Length)
                ? _displayRendererBaseColors[i]
                : Color.cyan;
            baseCol.a = alpha * 0.3f;
            
            bool hasColor = _displayRendererHasColor != null && i < _displayRendererHasColor.Length && _displayRendererHasColor[i];
            bool hasBaseColor = _displayRendererHasBaseColor != null && i < _displayRendererHasBaseColor.Length && _displayRendererHasBaseColor[i];
            if (hasColor) _propBlock.SetColor("_Color", baseCol);
            if (hasBaseColor) _propBlock.SetColor("_BaseColor", baseCol);
            _propBlock.SetFloat("_Alpha", alpha);
            r.SetPropertyBlock(_propBlock);
        }

        if (fuelBarFillRenderer != null)
        {
            Color targetColor = GetFuelColorForCurrentRatio();
            Color holoFillColor = targetColor;
            holoFillColor.a = 0.6f * alpha;

            fuelBarFillRenderer.GetPropertyBlock(_propBlock);
            if (_fuelBarHasColor) _propBlock.SetColor("_Color", holoFillColor);
            if (_fuelBarHasBaseColor) _propBlock.SetColor("_BaseColor", holoFillColor);
            _propBlock.SetFloat("_Alpha", alpha);
            fuelBarFillRenderer.SetPropertyBlock(_propBlock);
        }

        if (fuelText != null)
        {
            Color targetColor = GetFuelColorForCurrentRatio();
            int percent = Mathf.RoundToInt(_currentRatio * 100f);
            if (percent != _lastFuelTextPercent)
            {
                _lastFuelTextPercent = percent;
                fuelText.text = $"{percent}%";
            }

            fuelText.color = new Color(targetColor.r, targetColor.g, targetColor.b, alpha * 0.5f);
        }
    }

    // ── 🌟 核心新增：专门更新车内中控屏文本的私有函数 ───────────────────────
    private void UpdateCarDashboardText()
    {
        // 计算文本和根据油量改变颜色
        int percent = Mathf.RoundToInt(_currentRatio * 100f);
        bool percentChanged = percent != _lastDashboardPercent;
        bool colorChanged = Mathf.Abs(_lastDashboardColorRatio - _currentRatio) > 0.001f;
        if (!percentChanged && !colorChanged)
        {
            return;
        }

        string displayText = $"{percent}%";

        Color targetColor = GetFuelColorForCurrentRatio();

        _lastDashboardPercent = percent;
        _lastDashboardColorRatio = _currentRatio;

        // 情况一：如果你拖入的是 UI Canvas 里的 TextMeshPro
        if (carDashboardFuelText != null)
        {
            carDashboardFuelText.text = displayText;
            carDashboardFuelText.color = targetColor;
        }

        // 情况二：如果你拖入的是直接贴在 3D 屏幕模型上的 TextMeshPro
        if (carDashboardFuelText3D != null)
        {
            carDashboardFuelText3D.text = displayText;
            carDashboardFuelText3D.color = targetColor;
        }
    }

    private Color GetFuelColorForCurrentRatio()
    {
        if (!Mathf.Approximately(_cachedFuelColorRatio, _currentRatio))
        {
            _cachedFuelColorRatio = _currentRatio;
            _cachedFuelColor = _currentRatio < 0.3f
                ? Color.Lerp(lowFuelColor, mediumFuelColor, _currentRatio / 0.3f)
                : Color.Lerp(mediumFuelColor, fullFuelColor, (_currentRatio - 0.3f) / 0.7f);
        }

        return _cachedFuelColor;
    }
    // ────────────────────────────────────────────────────────────────────────

    public void ShowUI(bool isLooking)
    {
        enabled = true;
        if (isLooking)
        {
            _lookAwayTimer = 0f;
        }
        else if (_isLookingAt)
        {
            _lookAwayTimer = 1.0f;
        }
        _isLookingAt = isLooking;
    }

    public bool AddFuel(float amount)
    {
        if (currentFuel >= maxCapacity)
            return false;

        currentFuel += amount;
        return true;
    }

    public void UpdateUI()
    {
        float ratio = maxCapacity > 0f ? Mathf.Clamp01(currentFuel / maxCapacity) : 0f;
        _targetRatio = ratio;
        enabled = true;
    }
}


