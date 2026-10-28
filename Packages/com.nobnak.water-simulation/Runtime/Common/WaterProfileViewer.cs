using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Displays a 1D cross-section profile of the water surface and bed level along a slice line.
    /// Uses asynchronous GPU readback (AsyncGPUReadback) and caches static bathymetry to avoid GPU pipeline stalls.
    /// </summary>
    [ExecuteAlways]
    public class WaterProfileViewer : MonoBehaviour {
        public enum ProfileAxis {
            XAxis = 0,
            ZAxis = 1
        }

        #region serialized fields
        [Header("Target Simulation")]
        [Tooltip("Simulation to profile.")]
        [SerializeField] private WaterSimulationBase targetSimulation;

        [Header("Profile Configuration")]
        [Tooltip("Cross section orientation.")]
        [SerializeField] private ProfileAxis sliceAxis = ProfileAxis.XAxis;

        [Tooltip("Normalized offset on perpendicular axis [0..1] (0.5 = center).")]
        [Range(0f, 1f)]
        [SerializeField] private float slicePosition = 0.5f;

        [Tooltip("Number of sampling points along the profile line.")]
        [Range(32, 1024)]
        [SerializeField] private int sampleCount = 256;

        [Tooltip("Vertical height exaggeration scale for profile display.")]
        [SerializeField] private float verticalScale = 1.0f;

        [Tooltip("Display offset in world space.")]
        [SerializeField] private Vector3 displayOffset = new Vector3(0f, 2f, 0f);

        [Tooltip("Minimum interval between GPU readback requests [s] (0 = once per simulation step).")]
        [Range(0f, 0.2f)]
        [SerializeField] private float readbackInterval = 0f;

        [Tooltip("Use synchronous readback only when AsyncGPUReadback is unsupported or forced for immediate single-frame capture.")]
        [SerializeField] private bool forceSynchronousReadback = false;

        [Header("Renderers")]
        [SerializeField] private LineRenderer surfaceLine;
        [SerializeField] private LineRenderer bedLine;

        [Header("Appearance")]
        [SerializeField] private Color surfaceColor = new Color(0.2f, 0.8f, 1.0f, 0.9f);
        [SerializeField] private Color bedColor = new Color(0.8f, 0.6f, 0.3f, 0.7f);
        [SerializeField] private float lineWidth = 0.15f;
        #endregion

        #region private fields
        private Vector3[] surfacePoints;
        private Vector3[] bedPoints;
        private float[] cachedHeights;
        private float[] cachedBed;
        private Texture2D syncReadbackTex;
        private RenderTexture waterSliceRT;
        private RenderTexture bathySliceRT;

        private bool waterReadbackPending;
        private bool bathyReadbackPending;
        private int lastRequestedStep = -1;
        private float lastRequestTime = -1f;
        private int cachedBathyVersion = -1;
        private ProfileAxis cachedBathyAxis;
        private float cachedBathyPosition = -1f;
        private int cachedBathySampleCount = -1;

        private int readbackSampleStep = -1;
        private float readbackSampleSimTime;
        #endregion

        #region properties
        public WaterSimulationBase TargetSimulation {
            get => targetSimulation;
            set {
                targetSimulation = value;
                InvalidateBathymetryCache();
            }
        }

        public ProfileAxis Axis {
            get => sliceAxis;
            set {
                if (sliceAxis == value) return;
                sliceAxis = value;
                InvalidateBathymetryCache();
            }
        }

        public float SlicePosition {
            get => slicePosition;
            set {
                float clamped = math.clamp(value, 0f, 1f);
                if (math.abs(slicePosition - clamped) < 1e-6f) return;
                slicePosition = clamped;
                InvalidateBathymetryCache();
            }
        }

        public float VerticalScale {
            get => verticalScale;
            set => verticalScale = value;
        }

        public bool ForceSynchronousReadback {
            get => forceSynchronousReadback;
            set => forceSynchronousReadback = value;
        }

        public int ReadbackSampleStep => readbackSampleStep;
        public float ReadbackSampleSimTime => readbackSampleSimTime;
        #endregion

        #region Unity lifecycle
        private void OnEnable() {
            InvalidateBathymetryCache();
            lastRequestedStep = -1;
            waterReadbackPending = false;
            bathyReadbackPending = false;
            EnsureLineRenderers();
        }

        private void OnDisable() {
            AsyncGPUReadback.WaitAllRequests();
            waterReadbackPending = false;
            bathyReadbackPending = false;

            if (waterSliceRT != null) {
                waterSliceRT.Release();
                DestroyImmediate(waterSliceRT);
                waterSliceRT = null;
            }
            if (bathySliceRT != null) {
                bathySliceRT.Release();
                DestroyImmediate(bathySliceRT);
                bathySliceRT = null;
            }
            if (syncReadbackTex != null) {
                DestroyImmediate(syncReadbackTex);
                syncReadbackTex = null;
            }
        }

        private void LateUpdate() {
            if (targetSimulation == null || targetSimulation.WaterDataTexture == null) return;
            UpdateProfile();
        }
        #endregion

        #region public interface
        public void Configure(WaterSimulationBase simulation, ProfileAxis axis, float positionNorm) {
            targetSimulation = simulation;
            sliceAxis = axis;
            slicePosition = math.clamp(positionNorm, 0f, 1f);
            InvalidateBathymetryCache();
        }

        public void InvalidateBathymetryCache() {
            cachedBathyVersion = -1;
        }
        #endregion

        #region private methods
        private void EnsureLineRenderers() {
            if (surfaceLine == null) {
                var surfObj = new GameObject("SurfaceLine");
                surfObj.transform.SetParent(transform, false);
                surfaceLine = surfObj.AddComponent<LineRenderer>();
            }
            ConfigureLineRenderer(surfaceLine, surfaceColor);

            if (bedLine == null) {
                var bedObj = new GameObject("BedLine");
                bedObj.transform.SetParent(transform, false);
                bedLine = bedObj.AddComponent<LineRenderer>();
            }
            ConfigureLineRenderer(bedLine, bedColor);
        }

        private void ConfigureLineRenderer(LineRenderer lr, Color color) {
            lr.useWorldSpace = true;
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.startColor = color;
            lr.endColor = color;

            if (lr.sharedMaterial == null) {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader != null) {
                    var mat = new Material(shader) {
                        color = color
                    };
                    lr.sharedMaterial = mat;
                }
            }
        }

        private void UpdateProfile() {
            EnsureLineRenderers();
            EnsureArrays();

            RenderTexture waterTex = targetSimulation.WaterDataTexture;
            RenderTexture bathyTex = targetSimulation.BathymetryTexture;

            RequestOrPerformReadbacks(waterTex, bathyTex);

            Vector2 area = targetSimulation.AreaSize;
            Vector3 center = targetSimulation.transform.position + displayOffset;
            float halfX = area.x * 0.5f;
            float halfZ = area.y * 0.5f;

            for (int i = 0; i < sampleCount; i++) {
                float t = (float)i / (sampleCount - 1);
                float wx, wz;

                if (sliceAxis == ProfileAxis.XAxis) {
                    wx = math.lerp(-halfX, halfX, t);
                    wz = math.lerp(-halfZ, halfZ, slicePosition);
                } else {
                    wx = math.lerp(-halfX, halfX, slicePosition);
                    wz = math.lerp(-halfZ, halfZ, t);
                }

                float eta = cachedHeights[i];
                float bed = cachedBed[i];

                surfacePoints[i] = center + new Vector3(wx, eta * verticalScale, wz);
                bedPoints[i] = center + new Vector3(wx, bed * verticalScale, wz);
            }

            surfaceLine.positionCount = sampleCount;
            surfaceLine.SetPositions(surfacePoints);

            if (bathyTex != null) {
                bedLine.enabled = true;
                bedLine.positionCount = sampleCount;
                bedLine.SetPositions(bedPoints);
            } else {
                bedLine.enabled = false;
            }
        }

        private void EnsureArrays() {
            if (surfacePoints == null || surfacePoints.Length != sampleCount) {
                surfacePoints = new Vector3[sampleCount];
                bedPoints = new Vector3[sampleCount];
                cachedHeights = new float[sampleCount];
                cachedBed = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++) {
                    cachedBed[i] = -2f;
                }
                InvalidateBathymetryCache();
            }
        }

        private void EnsureSliceTextures() {
            if (waterSliceRT == null || waterSliceRT.width != sampleCount) {
                if (waterSliceRT != null) {
                    waterSliceRT.Release();
                    DestroyImmediate(waterSliceRT);
                }
                waterSliceRT = new RenderTexture(sampleCount, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {
                    filterMode = FilterMode.Bilinear
                };
                waterSliceRT.Create();
            }

            if (bathySliceRT == null || bathySliceRT.width != sampleCount) {
                if (bathySliceRT != null) {
                    bathySliceRT.Release();
                    DestroyImmediate(bathySliceRT);
                }
                bathySliceRT = new RenderTexture(sampleCount, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {
                    filterMode = FilterMode.Bilinear
                };
                bathySliceRT.Create();
            }
        }

        private void RequestOrPerformReadbacks(RenderTexture waterTex, RenderTexture bathyTex) {
            EnsureSliceTextures();

            Vector2 scale = (sliceAxis == ProfileAxis.XAxis) ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
            Vector2 offset = (sliceAxis == ProfileAxis.XAxis) ? new Vector2(0f, slicePosition) : new Vector2(slicePosition, 0f);

            bool useSync = forceSynchronousReadback || !SystemInfo.supportsAsyncGPUReadback || !Application.isPlaying;

            // 1. Water surface readback (only when simulation stepped or forced)
            int currentStep = targetSimulation.StepCount;
            float now = Time.unscaledTime;
            bool stepChanged = (currentStep != lastRequestedStep) || (readbackSampleStep < 0);
            bool intervalElapsed = (readbackInterval <= 0f) || (now - lastRequestTime >= readbackInterval);

            if (useSync) {
                if (stepChanged) {
                    Graphics.Blit(waterTex, waterSliceRT, scale, offset);
                    ReadSliceSynchronously(waterSliceRT, cachedHeights);
                    lastRequestedStep = currentStep;
                    lastRequestTime = now;
                    readbackSampleStep = currentStep;
                    readbackSampleSimTime = targetSimulation.SimulationTime;
                }
            } else if (!waterReadbackPending && stepChanged && intervalElapsed) {
                Graphics.Blit(waterTex, waterSliceRT, scale, offset);
                int capturedStep = currentStep;
                float capturedSimTime = targetSimulation.SimulationTime;
                lastRequestedStep = currentStep;
                lastRequestTime = now;
                waterReadbackPending = true;

                AsyncGPUReadback.Request(waterSliceRT, 0, TextureFormat.RGBAFloat, request => {
                    waterReadbackPending = false;
                    if (request.hasError || cachedHeights == null) return;
                    NativeArray<float4> data = request.GetData<float4>();
                    int count = math.min(cachedHeights.Length, data.Length);
                    for (int i = 0; i < count; i++) {
                        cachedHeights[i] = data[i].y; // G channel: elevation eta
                    }
                    readbackSampleStep = capturedStep;
                    readbackSampleSimTime = capturedSimTime;
                });
            }

            // 2. Bathymetry readback (cached until BathymetryVersion or slice config changes)
            if (bathyTex != null) {
                int bathyVer = targetSimulation.BathymetryVersion;
                bool bathyNeedsUpdate = (bathyVer != cachedBathyVersion)
                    || (sliceAxis != cachedBathyAxis)
                    || (math.abs(slicePosition - cachedBathyPosition) > 1e-6f)
                    || (sampleCount != cachedBathySampleCount);

                if (useSync) {
                    if (bathyNeedsUpdate) {
                        Graphics.Blit(bathyTex, bathySliceRT, scale, offset);
                        ReadSliceSynchronously(bathySliceRT, cachedBed);
                        cachedBathyVersion = bathyVer;
                        cachedBathyAxis = sliceAxis;
                        cachedBathyPosition = slicePosition;
                        cachedBathySampleCount = sampleCount;
                    }
                } else if (!bathyReadbackPending && bathyNeedsUpdate) {
                    Graphics.Blit(bathyTex, bathySliceRT, scale, offset);
                    int targetVer = bathyVer;
                    ProfileAxis targetAxis = sliceAxis;
                    float targetPos = slicePosition;
                    int targetCount = sampleCount;
                    bathyReadbackPending = true;

                    AsyncGPUReadback.Request(bathySliceRT, 0, TextureFormat.RGBAFloat, request => {
                        bathyReadbackPending = false;
                        if (request.hasError || cachedBed == null) return;
                        NativeArray<float4> data = request.GetData<float4>();
                        int count = math.min(cachedBed.Length, data.Length);
                        for (int i = 0; i < count; i++) {
                            cachedBed[i] = data[i].y; // G channel: bed level z_b = -h
                        }
                        cachedBathyVersion = targetVer;
                        cachedBathyAxis = targetAxis;
                        cachedBathyPosition = targetPos;
                        cachedBathySampleCount = targetCount;
                    });
                }
            }
        }

        private void ReadSliceSynchronously(RenderTexture sourceRT, float[] destArray) {
            if (syncReadbackTex == null || syncReadbackTex.width != sampleCount) {
                if (syncReadbackTex != null) DestroyImmediate(syncReadbackTex);
                syncReadbackTex = new Texture2D(sampleCount, 1, TextureFormat.RGBAFloat, false, true);
            }

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = sourceRT;
            syncReadbackTex.ReadPixels(new Rect(0, 0, sampleCount, 1), 0, 0, false);
            syncReadbackTex.Apply(false);
            RenderTexture.active = prev;

            NativeArray<float4> raw = syncReadbackTex.GetRawTextureData<float4>();
            int count = math.min(destArray.Length, raw.Length);
            for (int i = 0; i < count; i++) {
                destArray[i] = raw[i].y;
            }
        }
        #endregion
    }
}
