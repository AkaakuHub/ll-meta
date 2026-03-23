using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Utils;
using Media3DPoint3D = System.Windows.Media.Media3D.Point3D;
using Media3DVector3D = System.Windows.Media.Media3D.Vector3D;

namespace OpenKikaiSan.App.Controls;

public partial class OpenXrSceneView : UserControl
{
    private const float ButtonTravel = 0.12f;
    private const float TriggerTravelRadians = 0.28f;
    private const float GripTravel = 0.16f;
    private const float ThumbstickTiltRadians = 0.32f;
    private const float ThumbstickPressTravel = 0.08f;
    private const float HeadPositionScale = 10.0f;
    private const float SceneVerticalOffset = 7.0f;
    private static readonly Vector3 HmdBaseTranslation = new(
        0.0f,
        -1.4f + SceneVerticalOffset,
        0.0f
    );
    private static readonly Quaternion OpenXrToSceneBasisRotation = Quaternion.CreateFromAxisAngle(
        Vector3.UnitY,
        MathF.PI
    );
    private static readonly Vector4 ActiveGlowColor = new(0.18f, 0.42f, 1.35f, 1.0f);
    private static readonly Vector4 ActiveTintColor = new(0.24f, 0.5f, 1.0f, 1.0f);
    private static readonly object NullMaterialColorValue = new();
    private static readonly Media3DPoint3D DefaultCameraPosition = new(0, 25, -20);
    private static readonly Media3DVector3D DefaultCameraLookDirection = new(0, -35, 34);
    private static readonly Media3DVector3D DefaultCameraUpDirection = new(0, 1, 0);

    private readonly Dictionary<string, SceneNode> _nodesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<SceneNode, Matrix4x4> _baseLocalMatrices = [];
    private readonly Dictionary<
        (object Material, string PropertyName),
        object
    > _baseMaterialValues = new(TupleReferenceComparer.Instance);
    private readonly Stack<IEnumerator<SceneNode>> _sceneTraversalStack = new();
    private readonly Importer _importer = new();
    private readonly DefaultEffectsManager _effectsManager = new();
    private readonly AppLogger _logger = new();
    private GroupNode? _hmdPoseRootNode;
    private SceneNode? _hmdRootNode;
    private SceneNode? _leftControllerRootNode;
    private SceneNode? _rightControllerRootNode;
    private Quaternion _lastValidHmdRotation = Quaternion.Identity;
    private Vector3 _lastValidHmdTranslation = Vector3.Zero;
    private bool _isSceneReady;
    private bool _isInitializationScheduled;

    public OpenXrSceneView()
    {
        InitializeComponent();
        Viewport.EffectsManager = _effectsManager;
        Viewport.RenderExceptionOccurred += (_, e) =>
        {
            var exception =
                e.GetType().GetProperty("Exception")?.GetValue(e) as Exception
                ?? new InvalidOperationException(e.ToString());
            _logger.Error("OpenXrSceneView render exception occurred.", exception);
        };
        Viewport.PreviewMouseWheel += OnViewportPreviewMouseWheel;
        Viewport.PreviewMouseDown += OnViewportPreviewMouseDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(OpenXrControllerState),
        typeof(OpenXrSceneView),
        new PropertyMetadata(default(OpenXrControllerState), OnStateChanged)
    );

    public OpenXrControllerState State
    {
        get => (OpenXrControllerState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OpenXrSceneView view && e.NewValue is OpenXrControllerState state)
        {
            view.ApplyState(state);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.Debug(
            $"OpenXrSceneView loaded. ready={_isSceneReady} visible={IsVisible} width={ActualWidth:F0} height={ActualHeight:F0}"
        );
        ScheduleSceneInitialization();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _logger.Debug("OpenXrSceneView unloaded. Resetting scene state.");
        ResetSceneState();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Equals(e.NewValue, true))
        {
            ScheduleSceneInitialization();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            ScheduleSceneInitialization();
        }
    }

    private void OnViewportPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void OnViewportPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not MouseButton.Right)
        {
            e.Handled = true;
        }
    }

    private void ScheduleSceneInitialization()
    {
        if (_isSceneReady || _isInitializationScheduled || !IsLoaded)
        {
            return;
        }

        _isInitializationScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(InitializeSceneIfNeeded));
    }

    private void InitializeSceneIfNeeded()
    {
        _isInitializationScheduled = false;

        if (_isSceneReady || !IsLoaded || !IsVisible)
        {
            return;
        }

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            _logger.Debug(
                $"OpenXrSceneView initialization deferred. width={ActualWidth:F0} height={ActualHeight:F0}"
            );
            ScheduleSceneInitialization();
            return;
        }

        try
        {
            _logger.Debug(
                $"OpenXrSceneView initializing scene. width={ActualWidth:F0} height={ActualHeight:F0}"
            );
            LoadScene();
            ApplyState(State);
            ResetCamera();
            Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    try
                    {
                        Viewport.InvalidateRender();
                        _logger.Debug("OpenXrSceneView render invalidated after camera reset.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("OpenXrSceneView render invalidation failed.", ex);
                    }
                })
            );
            _logger.Debug("OpenXrSceneView scene initialized.");
        }
        catch (Exception ex)
        {
            _logger.Error("OpenXrSceneView scene initialization failed.", ex);
            ResetSceneState();
        }
    }

    private void ResetSceneState()
    {
        _isInitializationScheduled = false;
        _isSceneReady = false;
        _hmdPoseRootNode = null;
        _hmdRootNode = null;
        _leftControllerRootNode = null;
        _rightControllerRootNode = null;
        _lastValidHmdRotation = Quaternion.Identity;
        _lastValidHmdTranslation = Vector3.Zero;
        _nodesByName.Clear();
        _baseLocalMatrices.Clear();
        _baseMaterialValues.Clear();
        SceneRoot.Clear(false);
    }

    private void ResetCamera()
    {
        if (Viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
        {
            camera.Position = DefaultCameraPosition;
            camera.LookDirection = DefaultCameraLookDirection;
            camera.UpDirection = DefaultCameraUpDirection;
            _logger.Debug("OpenXrSceneView camera reset applied.");
        }
    }

    private void LoadScene()
    {
        ResetSceneState();

        _hmdRootNode = LoadModelRoot("low_poly_Quest3HMD.glb", MaterialType.Auto);
        _hmdPoseRootNode = new GroupNode();
        _hmdPoseRootNode.AddChildNode(_hmdRootNode);
        _leftControllerRootNode = LoadModelRoot(
            "low_poly_oculus_controller_plus_L.glb",
            MaterialType.Auto
        );
        _rightControllerRootNode = LoadModelRoot(
            "low_poly_oculus_controller_plus_R.glb",
            MaterialType.Auto
        );

        ApplyStaticShowcaseLayout();

        AddScene(_hmdPoseRootNode);
        AddScene(_leftControllerRootNode);
        AddScene(_rightControllerRootNode);
        PrepareInteractiveNodeMaterials();

        _isSceneReady = true;
    }

    private SceneNode LoadModelRoot(string fileName, MaterialType materialType)
    {
        var assetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);
        var scene =
            _importer.Load(
                assetPath,
                new ImporterConfiguration
                {
                    ImportAnimations = false,
                    ImportMaterialType = materialType,
                }
            ) ?? throw new InvalidOperationException($"Failed to load scene from {assetPath}.");
        return scene.Root
            ?? throw new InvalidOperationException($"Failed to load scene root from {assetPath}.");
    }

    private void ApplyStaticShowcaseLayout()
    {
        if (_hmdPoseRootNode is not null)
        {
            _hmdPoseRootNode.ModelMatrix = Matrix4x4.CreateTranslation(HmdBaseTranslation);
        }

        if (_leftControllerRootNode is not null)
        {
            _leftControllerRootNode.ModelMatrix = CreateStaticRootMatrix(
                _leftControllerRootNode.ModelMatrix,
                new Vector3(7.5f, -1.2f + SceneVerticalOffset, 5.2f),
                0.55f
            );
        }

        if (_rightControllerRootNode is not null)
        {
            _rightControllerRootNode.ModelMatrix = CreateStaticRootMatrix(
                _rightControllerRootNode.ModelMatrix,
                new Vector3(-7.5f, -1.2f + SceneVerticalOffset, 5.2f),
                -0.55f
            );
        }
    }

    private void AddScene(SceneNode rootNode)
    {
        SceneRoot.AddNode(rootNode);

        foreach (
            var node in HelixToolkit.SharpDX.TreeTraverser.PreorderDFT(
                new[] { rootNode },
                _ => true,
                _sceneTraversalStack
            )
        )
        {
            _baseLocalMatrices[node] = node.ModelMatrix;
            ApplyOutline(node);

            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                _nodesByName[node.Name] = node;
            }
        }
    }

    private static void ApplyOutline(SceneNode node)
    {
        if (node is GeometryNode geometryNode)
        {
            geometryNode.PostEffects = "controllerOutline";
        }
    }

    private void PrepareInteractiveNodeMaterials()
    {
        var interactiveNodeNames = new[]
        {
            "b_button_x",
            "b_button_y",
            "b_button_a",
            "b_button_b",
            "left_b_trigger_front",
            "right_b_trigger_front",
            "left_b_trigger_grip",
            "right_b_trigger_grip",
            "left_b_thumbstick",
            "right_b_thumbstick",
        };

        foreach (var nodeName in interactiveNodeNames)
        {
            if (!TryGetNode(nodeName, out var node, out _))
            {
                continue;
            }

            foreach (
                var targetNode in HelixToolkit.SharpDX.TreeTraverser.PreorderDFT(
                    new[] { node },
                    _ => true,
                    _sceneTraversalStack
                )
            )
            {
                DetachSharedMaterial(targetNode);
            }
        }
    }

    private static void DetachSharedMaterial(SceneNode node)
    {
        var materialProperty = node.GetType()
            .GetProperty("Material", BindingFlags.Instance | BindingFlags.Public);
        if (
            materialProperty is null
            || !materialProperty.CanWrite
            || materialProperty.GetValue(node) is not object material
        )
        {
            return;
        }

        materialProperty.SetValue(node, CloneMaterial(material));
    }

    private static object CloneMaterial(object sourceMaterial)
    {
        var materialType = sourceMaterial.GetType();
        var clone =
            Activator.CreateInstance(materialType)
            ?? throw new InvalidOperationException(
                $"Failed to clone material type {materialType.FullName}."
            );

        foreach (
            var property in materialType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
        )
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            try
            {
                property.SetValue(clone, property.GetValue(sourceMaterial));
            }
            catch { }
        }

        return clone;
    }

    private void ApplyState(OpenXrControllerState state)
    {
        if (!_isSceneReady)
        {
            return;
        }

        ApplyHmdPose(state);
        ApplyButtonPress("b_button_x", state.LeftXPressed);
        ApplyButtonPress("b_button_y", state.LeftYPressed);
        ApplyButtonPress("b_button_a", state.RightAPressed);
        ApplyButtonPress("b_button_b", state.RightBPressed);
        ApplyTrigger("left_b_trigger_front", state.LeftTriggerValue, 1.0f);
        ApplyTrigger("right_b_trigger_front", state.RightTriggerValue, -1.0f);
        ApplyGrip("left_b_trigger_grip", state.LeftGripValue, 1.0f);
        ApplyGrip("right_b_trigger_grip", state.RightGripValue, -1.0f);
        ApplyThumbstick(
            "left_b_thumbstick",
            state.LeftStickX,
            state.LeftStickY,
            state.LeftStickClickPressed
        );
        ApplyThumbstick(
            "right_b_thumbstick",
            state.RightStickX,
            state.RightStickY,
            state.RightStickClickPressed
        );
    }

    private void ApplyHmdPose(OpenXrControllerState state)
    {
        if (_hmdPoseRootNode is null)
        {
            return;
        }

        var runtimeRotation = Quaternion.Identity;
        var runtimeTranslation = Vector3.Zero;

        if (state.HeadPose.IsOrientationValid && state.HeadPose.IsOrientationTracked)
        {
            var openXrOrientation = Quaternion.Normalize(
                new Quaternion(
                    state.HeadPose.OrientationX,
                    state.HeadPose.OrientationY,
                    state.HeadPose.OrientationZ,
                    state.HeadPose.OrientationW
                )
            );
            var sceneOrientation = Quaternion.Normalize(
                OpenXrToSceneBasisRotation
                    * openXrOrientation
                    * Quaternion.Conjugate(OpenXrToSceneBasisRotation)
            );
            runtimeRotation = sceneOrientation;
            _lastValidHmdRotation = runtimeRotation;
        }
        else
        {
            runtimeRotation = _lastValidHmdRotation;
        }

        if (state.HeadPose.IsPositionValid && state.HeadPose.IsPositionTracked)
        {
            runtimeTranslation = Vector3.Transform(
                new Vector3(
                    state.HeadPose.PositionX,
                    state.HeadPose.PositionY,
                    state.HeadPose.PositionZ
                ),
                Matrix4x4.CreateFromQuaternion(OpenXrToSceneBasisRotation)
            );
            _lastValidHmdTranslation = runtimeTranslation;
        }
        else
        {
            runtimeTranslation = _lastValidHmdTranslation;
        }

        _hmdPoseRootNode.ModelMatrix =
            Matrix4x4.CreateFromQuaternion(runtimeRotation)
            * Matrix4x4.CreateTranslation(
                HmdBaseTranslation + (runtimeTranslation * HeadPositionScale)
            );
    }

    private static Matrix4x4 CreateStaticRootMatrix(
        Matrix4x4 originalMatrix,
        Vector3 translation,
        float yawRadians
    )
    {
        if (!Matrix4x4.Decompose(originalMatrix, out var scale, out var rotation, out _))
        {
            return originalMatrix;
        }

        var adjustedRotation = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians) * rotation
        );

        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(adjustedRotation)
            * Matrix4x4.CreateTranslation(translation);
    }

    private void ApplyButtonPress(string nodeName, bool isPressed)
    {
        if (!TryGetNode(nodeName, out var node, out var baseMatrix))
        {
            return;
        }

        var delta = isPressed
            ? Matrix4x4.CreateTranslation(0.0f, -ButtonTravel, 0.0f)
            : Matrix4x4.Identity;

        node.ModelMatrix = ApplyLocalNodeTransform(baseMatrix, delta);
        SetNodeGlow(node, isPressed ? 1.0f : 0.0f);
    }

    private void ApplyTrigger(string nodeName, float value, float direction)
    {
        if (!TryGetNode(nodeName, out var node, out var baseMatrix))
        {
            return;
        }

        var rotation = Matrix4x4.CreateRotationX(
            -direction * TriggerTravelRadians * Clamp01(value)
        );
        node.ModelMatrix = ApplyLocalNodeTransform(baseMatrix, rotation);
        SetNodeGlow(node, Clamp01(value));
    }

    private void ApplyGrip(string nodeName, float value, float direction)
    {
        if (!TryGetNode(nodeName, out var node, out var baseMatrix))
        {
            return;
        }

        var delta = Matrix4x4.CreateTranslation(
            direction * GripTravel * Clamp01(value),
            0.0f,
            0.0f
        );
        node.ModelMatrix = ApplyLocalNodeTransform(baseMatrix, delta);
        SetNodeGlow(node, Clamp01(value));
    }

    private void ApplyThumbstick(string nodeName, float x, float y, bool isPressed)
    {
        if (!TryGetNode(nodeName, out var node, out var baseMatrix))
        {
            return;
        }

        var tiltX = Matrix4x4.CreateRotationX(ClampStick(y) * ThumbstickTiltRadians);
        var tiltZ = Matrix4x4.CreateRotationZ(ClampStick(x) * ThumbstickTiltRadians);
        var press = isPressed
            ? Matrix4x4.CreateTranslation(0.0f, -ThumbstickPressTravel, 0.0f)
            : Matrix4x4.Identity;

        node.ModelMatrix = ApplyLocalNodeTransform(baseMatrix, tiltX * tiltZ * press);
        var glowAmount = Math.Max(Math.Abs(ClampStick(x)), Math.Abs(ClampStick(y)));
        if (isPressed)
        {
            glowAmount = Math.Max(glowAmount, 1.0f);
        }
        SetNodeGlow(node, glowAmount);
    }

    private bool TryGetNode(string nodeName, out SceneNode node, out Matrix4x4 baseMatrix)
    {
        if (
            _nodesByName.TryGetValue(nodeName, out node!)
            && _baseLocalMatrices.TryGetValue(node, out baseMatrix)
        )
        {
            return true;
        }

        node = null!;
        baseMatrix = Matrix4x4.Identity;
        return false;
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0.0f, 1.0f);
    }

    private static float ClampStick(float value)
    {
        return Math.Clamp(value, -1.0f, 1.0f);
    }

    private static Matrix4x4 ApplyLocalNodeTransform(Matrix4x4 baseMatrix, Matrix4x4 localTransform)
    {
        if (!Matrix4x4.Decompose(baseMatrix, out var scale, out var rotation, out var translation))
        {
            return baseMatrix;
        }

        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * localTransform
            * Matrix4x4.CreateTranslation(translation);
    }

    private void SetNodeGlow(SceneNode node, float glowAmount)
    {
        foreach (
            var targetNode in HelixToolkit.SharpDX.TreeTraverser.PreorderDFT(
                new[] { node },
                _ => true,
                _sceneTraversalStack
            )
        )
        {
            ApplyGlowToMaterial(targetNode, glowAmount);
        }

        if (glowAmount > 0.01f)
        {
            Viewport.InvalidateRender();
        }
    }

    private void ApplyGlowToMaterial(SceneNode node, float glowAmount)
    {
        var materialProperty = node.GetType()
            .GetProperty("Material", BindingFlags.Instance | BindingFlags.Public);
        if (materialProperty?.GetValue(node) is not object material)
        {
            return;
        }

        var emissiveProperty = material
            .GetType()
            .GetProperty("EmissiveColor", BindingFlags.Instance | BindingFlags.Public);
        if (emissiveProperty is null || !emissiveProperty.CanWrite)
        {
            return;
        }

        ApplyMaterialColorBlend(material, "EmissiveColor", ActiveGlowColor, glowAmount);
        ApplyMaterialColorBlend(material, "AlbedoColor", ActiveTintColor, glowAmount * 0.55f);
        ApplyMaterialColorBlend(material, "DiffuseColor", ActiveTintColor, glowAmount * 0.55f);
        ApplyMaterialColorBlend(material, "SpecularColor", ActiveTintColor, glowAmount * 0.25f);
    }

    private void ApplyMaterialColorBlend(
        object material,
        string propertyName,
        Vector4 targetColor,
        float blendAmount
    )
    {
        var property = material
            .GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        var key = (material, propertyName);
        if (!_baseMaterialValues.ContainsKey(key))
        {
            _baseMaterialValues[key] = property.GetValue(material) ?? NullMaterialColorValue;
        }

        var baseValue = _baseMaterialValues[key];
        if (blendAmount <= 0.0f)
        {
            property.SetValue(
                material,
                ReferenceEquals(baseValue, NullMaterialColorValue) ? null : baseValue
            );
            return;
        }

        if (ReferenceEquals(baseValue, NullMaterialColorValue))
        {
            return;
        }

        var baseColor = ReadColorValue(baseValue);
        var blendedColor = Vector4.Lerp(
            baseColor,
            targetColor,
            Math.Clamp(blendAmount, 0.0f, 1.0f)
        );
        property.SetValue(material, CreateColorValue(property.PropertyType, blendedColor));
    }

    private static Vector4 ReadColorValue(object colorValue)
    {
        var colorType = colorValue.GetType();
        return new Vector4(
            ReadColorChannel(colorType, colorValue, "Red", "R"),
            ReadColorChannel(colorType, colorValue, "Green", "G"),
            ReadColorChannel(colorType, colorValue, "Blue", "B"),
            ReadColorChannel(colorType, colorValue, "Alpha", "A", 1.0f)
        );
    }

    private static float ReadColorChannel(
        Type colorType,
        object colorValue,
        string primaryProperty,
        string fallbackProperty,
        float fallbackValue = 0.0f
    )
    {
        var property =
            colorType.GetProperty(primaryProperty, BindingFlags.Instance | BindingFlags.Public)
            ?? colorType.GetProperty(fallbackProperty, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(colorValue) is not object raw)
        {
            return fallbackValue;
        }

        return Convert.ToSingle(raw);
    }

    private static object CreateColorValue(Type colorType, Vector4 color)
    {
        var constructor = colorType.GetConstructor([
            typeof(float),
            typeof(float),
            typeof(float),
            typeof(float),
        ]);
        if (constructor is not null)
        {
            return constructor.Invoke([color.X, color.Y, color.Z, color.W]);
        }

        return Activator.CreateInstance(colorType)
            ?? throw new InvalidOperationException(
                $"Failed to create color value for {colorType.FullName}."
            );
    }

    private sealed class TupleReferenceComparer
        : IEqualityComparer<(object Material, string PropertyName)>
    {
        public static TupleReferenceComparer Instance { get; } = new();

        public bool Equals(
            (object Material, string PropertyName) x,
            (object Material, string PropertyName) y
        )
        {
            return ReferenceEquals(x.Material, y.Material)
                && string.Equals(x.PropertyName, y.PropertyName, StringComparison.Ordinal);
        }

        public int GetHashCode((object Material, string PropertyName) obj)
        {
            return HashCode.Combine(
                RuntimeHelpers.GetHashCode(obj.Material),
                StringComparer.Ordinal.GetHashCode(obj.PropertyName)
            );
        }
    }
}
