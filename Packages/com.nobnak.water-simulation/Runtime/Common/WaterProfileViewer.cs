using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Displays a 1D cross-section profile of the water surface and bed level along a slice line.
    /// Uses LineRenderer or OnGUI/Gizmos for clear visual comparison of wave dispersion and shape.
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
        private Texture2D readbackTexture;
        private RenderTexture sliceRT;
        #endregion

        #region properties
        public WaterSimulationBase TargetSimulation {
            get => targetSimulation;
            set => targetSimulation = value;
        }

        public ProfileAxis Axis {
            get => sliceAxis;
            set => sliceAxis = value;
        }

        public float SlicePosition {
            get => slicePosition;
            set => slicePosition = math.clamp(value, 0f, 1f);
        }

        public float VerticalScale {
            get => verticalScale;
            set => verticalScale = value;
        }
        #endregion

        #region Unity lifecycle
        private void OnEnable() {
            EnsureLineRenderers();
        }

        private void OnDisable() {
            if (sliceRT != null) {
                sliceRT.Release();
                DestroyImmediate(sliceRT);
                sliceRT = null;
            }
            if (readbackTexture != null) {
                DestroyImmediate(readbackTexture);
                readbackTexture = null;
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

            Vector2 area = targetSimulation.AreaSize;
            Vector3 center = targetSimulation.transform.position + displayOffset;
            float halfX = area.x * 0.5f;
            float halfZ = area.y * 0.5f;

            // Sample via texture blit or CPU readback
            RenderTexture waterTex = targetSimulation.WaterDataTexture;
            RenderTexture bathyTex = targetSimulation.BathymetryTexture;

            SampleTextures(waterTex, bathyTex);

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

                float eta = SampleHeightFromReadback(i);
                float bed = SampleBedFromReadback(i);

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
            }
        }

        private void SampleTextures(RenderTexture waterTex, RenderTexture bathyTex) {
            if (sliceRT == null || sliceRT.width != sampleCount) {
                if (sliceRT != null) {
                    sliceRT.Release();
                    DestroyImmediate(sliceRT);
                }
                sliceRT = new RenderTexture(sampleCount, 2, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) {
                    filterMode = FilterMode.Bilinear
                };
                sliceRT.Create();

                if (readbackTexture != null) DestroyImmediate(readbackTexture);
                readbackTexture = new Texture2D(sampleCount, 2, TextureFormat.RGBAHalf, false, true);
            }

            // Readback using Texture2D.ReadPixels
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(waterTex, sliceRT);
            RenderTexture.active = sliceRT;
            readbackTexture.ReadPixels(new Rect(0, 0, sampleCount, 2), 0, 0, false);
            readbackTexture.Apply(false);
            RenderTexture.active = prev;
        }

        private float SampleHeightFromReadback(int index) {
            if (readbackTexture == null) return 0f;
            Color c = readbackTexture.GetPixel(index, 0);
            return c.g; // G: elevation eta
        }

        private float SampleBedFromReadback(int index) {
            if (readbackTexture == null) return -2f;
            Color c = readbackTexture.GetPixel(index, 1);
            return c.g - 5f; // Static bed level offset
        }
        #endregion
    }
}
