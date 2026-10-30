using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Renders 3D bathymetry/seabed mesh underneath water simulation domain.
    /// Displaced by IWaterSimulation.BathymetryTexture on GPU vertex shader.
    /// Supports realistic sand/rock textures, depth contours, and basin side walls.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SeabedRenderer : MonoBehaviour {
        public enum VisualMode {
            RealisticSand = 0,
            BathymetryHeatmap = 1,
            TopographicWireframe = 2
        }

        #region serialized fields
        [Header("Target Simulation")]
        [Tooltip("The water simulation providing bathymetry data.")]
        [SerializeField] private WaterSimulationBase targetSimulation;

        [Header("Mesh Geometry")]
        [Tooltip("Grid resolution for seabed surface along X and Z.")]
        [SerializeField] private Vector2Int meshResolution = new Vector2Int(128, 128);

        [Tooltip("Generate vertical basin skirt walls around perimeter.")]
        [SerializeField] private bool includeBasinWalls = true;

        [Tooltip("Depth [m] of the basin bottom skirt.")]
        [SerializeField] private float basinBottomDepth = 12.0f;

        [Tooltip("Vertical relief exaggeration factor for bathymetry display.")]
        [SerializeField] private float heightExaggeration = 2.0f;

        [Header("Visual Settings")]
        [SerializeField] private VisualMode visualizationMode = VisualMode.RealisticSand;
        [SerializeField] private Material seabedMaterial;
        [SerializeField] private Color shallowColor = new Color(0.86f, 0.78f, 0.62f, 1.0f);
        [SerializeField] private Color deepColor = new Color(0.18f, 0.24f, 0.28f, 1.0f);
        [SerializeField] private Color wallColor = new Color(0.16f, 0.18f, 0.20f, 1.0f);
        [SerializeField] private float contourInterval = 1.0f;
        #endregion

        #region private fields
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh generatedMesh;
        private MaterialPropertyBlock propBlock;
        private Vector2 lastAreaSize;
        private Vector2Int lastMeshResolution;
        private bool lastIncludeBasinWalls;
        private float lastBasinBottomDepth;
        #endregion

        #region properties
        public WaterSimulationBase TargetSimulation {
            get => targetSimulation;
            set {
                targetSimulation = value;
                EnsureMesh();
                UpdateMaterialProperties();
            }
        }

        public VisualMode Mode {
            get => visualizationMode;
            set {
                visualizationMode = value;
                UpdateMaterialProperties();
            }
        }

        public Material SeabedMaterial {
            get => seabedMaterial;
            set {
                seabedMaterial = value;
                if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
                if (meshRenderer != null) {
                    meshRenderer.sharedMaterial = value;
                    meshRenderer.sharedMaterials = value != null ? new Material[] { value } : new Material[0];
                }
            }
        }

        public bool IncludeBasinWalls {
            get => includeBasinWalls;
            set {
                includeBasinWalls = value;
                EnsureMesh();
            }
        }

        public float HeightExaggeration {
            get => heightExaggeration;
            set {
                heightExaggeration = math.clamp(value, 1.0f, 5.0f);
                UpdateMaterialProperties();
            }
        }
        #endregion

        #region Unity lifecycle
        private void OnEnable() {
            meshFilter = GetComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            propBlock = new MaterialPropertyBlock();

            EnsureDefaultMaterial();
            EnsureMesh();
            UpdateMaterialProperties();
        }

        private void OnDisable() {
            if (generatedMesh != null) {
                DestroyImmediate(generatedMesh);
                generatedMesh = null;
            }
        }

        private void LateUpdate() {
            if (targetSimulation == null) return;

            Vector2 areaSize = targetSimulation.AreaSize;
            if (generatedMesh == null || areaSize != lastAreaSize || meshResolution != lastMeshResolution ||
                includeBasinWalls != lastIncludeBasinWalls || math.abs(basinBottomDepth - lastBasinBottomDepth) > 0.01f) {
                EnsureMesh();
            }

            UpdateMaterialProperties();
        }
        #endregion

        #region public interface
        public void SetTargetSimulation(WaterSimulationBase simulation) {
            targetSimulation = simulation;
            EnsureMesh();
            UpdateMaterialProperties();
        }

        public void SetVisualMode(VisualMode mode) {
            visualizationMode = mode;
            UpdateMaterialProperties();
        }

        public void UpdateMaterialProperties() {
            if (meshRenderer == null) return;
            if (propBlock == null) propBlock = new MaterialPropertyBlock();

            meshRenderer.GetPropertyBlock(propBlock);

            if (targetSimulation != null && targetSimulation.BathymetryTexture != null) {
                propBlock.SetTexture(PropBathymetryTex, targetSimulation.BathymetryTexture);
                propBlock.SetVector(PropAreaSize, new Vector4(targetSimulation.AreaSize.x, targetSimulation.AreaSize.y, 0f, 0f));
            }

            propBlock.SetFloat(PropVisualMode, (float)visualizationMode);
            propBlock.SetColor(PropShallowColor, shallowColor);
            propBlock.SetColor(PropDeepColor, deepColor);
            propBlock.SetColor(PropWallColor, wallColor);
            propBlock.SetFloat(PropContourInterval, contourInterval);
            propBlock.SetFloat(PropBasinBottomDepth, basinBottomDepth);
            propBlock.SetFloat(PropHeightExaggeration, heightExaggeration);

            meshRenderer.SetPropertyBlock(propBlock);
        }
        #endregion

        #region private methods
        private void EnsureDefaultMaterial() {
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (seabedMaterial == null && (meshRenderer.sharedMaterial == null || meshRenderer.sharedMaterials.Length == 0)) {
#if UNITY_EDITOR
                var assetMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Packages/com.nobnak.water-simulation/Runtime/Materials/M_Seabed.mat");
                if (assetMat != null) seabedMaterial = assetMat;
#endif
                if (seabedMaterial == null) {
                    var shader = Shader.Find("Nobnak/WaterSimulation/Seabed");
                    if (shader != null) {
                        seabedMaterial = new Material(shader) {
                            name = "M_Seabed_Default"
                        };
                    }
                }
            }

            if (seabedMaterial != null) {
                meshRenderer.sharedMaterial = seabedMaterial;
                meshRenderer.sharedMaterials = new Material[] { seabedMaterial };
            }
        }

        private void EnsureMesh() {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

            Vector2 size = targetSimulation != null ? targetSimulation.AreaSize : new Vector2(100f, 100f);
            int resX = math.clamp(meshResolution.x, 4, 512);
            int resZ = math.clamp(meshResolution.y, 4, 512);

            if (generatedMesh != null) {
                DestroyImmediate(generatedMesh);
            }

            generatedMesh = GenerateSeabedMesh(size.x, size.y, resX, resZ, includeBasinWalls, basinBottomDepth);
            meshFilter.sharedMesh = generatedMesh;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            lastAreaSize = size;
            lastMeshResolution = new Vector2Int(resX, resZ);
            lastIncludeBasinWalls = includeBasinWalls;
            lastBasinBottomDepth = basinBottomDepth;

            EnsureDefaultMaterial();
        }

        private Mesh GenerateSeabedMesh(float width, float length, int resX, int resZ, bool withWalls, float wallDepth) {
            var mesh = new Mesh {
                name = "SeabedMesh"
            };

            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var indices = new List<int>();

            float dx = width / (resX - 1);
            float dz = length / (resZ - 1);
            float startX = -width * 0.5f;
            float startZ = -length * 0.5f;

            // 1. Terrain surface grid
            for (int z = 0; z < resZ; z++) {
                for (int x = 0; x < resX; x++) {
                    verts.Add(new Vector3(startX + x * dx, 0f, startZ + z * dz));
                    normals.Add(Vector3.up);
                    uvs.Add(new Vector2((float)x / (resX - 1), (float)z / (resZ - 1)));
                    colors.Add(new Color(1f, 0f, 0f, 1f)); // color.r = 1.0 -> seabed surface
                }
            }

            for (int z = 0; z < resZ - 1; z++) {
                for (int x = 0; x < resX - 1; x++) {
                    int bl = z * resX + x;
                    int br = bl + 1;
                    int tl = bl + resX;
                    int tr = tl + 1;

                    indices.Add(bl);
                    indices.Add(tl);
                    indices.Add(br);

                    indices.Add(br);
                    indices.Add(tl);
                    indices.Add(tr);
                }
            }

            // 2. Perimeter basin skirt walls
            if (withWalls) {
                // South Wall (Z min, normal -Z)
                int baseS = verts.Count;
                for (int x = 0; x < resX; x++) {
                    float px = startX + x * dx;
                    float u = (float)x / (resX - 1);
                    verts.Add(new Vector3(px, 0f, startZ));
                    normals.Add(new Vector3(0f, 0f, -1f));
                    uvs.Add(new Vector2(u, 0f));
                    colors.Add(new Color(0.5f, 0f, 0f, 1f)); // skirt top

                    verts.Add(new Vector3(px, 0f, startZ));
                    normals.Add(new Vector3(0f, 0f, -1f));
                    uvs.Add(new Vector2(u, 0f));
                    colors.Add(new Color(0f, 0f, 0f, 1f)); // skirt bottom
                }
                for (int x = 0; x < resX - 1; x++) {
                    int t0 = baseS + x * 2;
                    int b0 = t0 + 1;
                    int t1 = t0 + 2;
                    int b1 = t0 + 3;
                    indices.Add(b0); indices.Add(t0); indices.Add(t1);
                    indices.Add(b0); indices.Add(t1); indices.Add(b1);
                }

                // North Wall (Z max, normal +Z)
                int baseN = verts.Count;
                for (int x = 0; x < resX; x++) {
                    float px = startX + x * dx;
                    float u = (float)x / (resX - 1);
                    verts.Add(new Vector3(px, 0f, startZ + length));
                    normals.Add(new Vector3(0f, 0f, 1f));
                    uvs.Add(new Vector2(u, 1f));
                    colors.Add(new Color(0.5f, 0f, 0f, 1f));

                    verts.Add(new Vector3(px, 0f, startZ + length));
                    normals.Add(new Vector3(0f, 0f, 1f));
                    uvs.Add(new Vector2(u, 1f));
                    colors.Add(new Color(0f, 0f, 0f, 1f));
                }
                for (int x = 0; x < resX - 1; x++) {
                    int t0 = baseN + x * 2;
                    int b0 = t0 + 1;
                    int t1 = t0 + 2;
                    int b1 = t0 + 3;
                    indices.Add(b1); indices.Add(t1); indices.Add(t0);
                    indices.Add(b1); indices.Add(t0); indices.Add(b0);
                }

                // West Wall (X min, normal -X)
                int baseW = verts.Count;
                for (int z = 0; z < resZ; z++) {
                    float pz = startZ + z * dz;
                    float v = (float)z / (resZ - 1);
                    verts.Add(new Vector3(startX, 0f, pz));
                    normals.Add(new Vector3(-1f, 0f, 0f));
                    uvs.Add(new Vector2(0f, v));
                    colors.Add(new Color(0.5f, 0f, 0f, 1f));

                    verts.Add(new Vector3(startX, 0f, pz));
                    normals.Add(new Vector3(-1f, 0f, 0f));
                    uvs.Add(new Vector2(0f, v));
                    colors.Add(new Color(0f, 0f, 0f, 1f));
                }
                for (int z = 0; z < resZ - 1; z++) {
                    int t0 = baseW + z * 2;
                    int b0 = t0 + 1;
                    int t1 = t0 + 2;
                    int b1 = t0 + 3;
                    indices.Add(b1); indices.Add(t1); indices.Add(t0);
                    indices.Add(b1); indices.Add(t0); indices.Add(b0);
                }

                // East Wall (X max, normal +X)
                int baseE = verts.Count;
                for (int z = 0; z < resZ; z++) {
                    float pz = startZ + z * dz;
                    float v = (float)z / (resZ - 1);
                    verts.Add(new Vector3(startX + width, 0f, pz));
                    normals.Add(new Vector3(1f, 0f, 0f));
                    uvs.Add(new Vector2(1f, v));
                    colors.Add(new Color(0.5f, 0f, 0f, 1f));

                    verts.Add(new Vector3(startX + width, 0f, pz));
                    normals.Add(new Vector3(1f, 0f, 0f));
                    uvs.Add(new Vector2(1f, v));
                    colors.Add(new Color(0f, 0f, 0f, 1f));
                }
                for (int z = 0; z < resZ - 1; z++) {
                    int t0 = baseE + z * 2;
                    int b0 = t0 + 1;
                    int t1 = t0 + 2;
                    int b1 = t0 + 3;
                    indices.Add(b0); indices.Add(t0); indices.Add(t1);
                    indices.Add(b0); indices.Add(t1); indices.Add(b1);
                }
            }

            if (verts.Count > 65535) {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
            mesh.bounds = new Bounds(new Vector3(0f, -wallDepth * 0.5f, 0f), new Vector3(width, wallDepth + 30f, length));
            return mesh;
        }
        #endregion

        #region constants / static
        private static readonly int PropBathymetryTex = Shader.PropertyToID("_BathymetryTex");
        private static readonly int PropAreaSize = Shader.PropertyToID("_AreaSize");
        private static readonly int PropVisualMode = Shader.PropertyToID("_VisualMode");
        private static readonly int PropShallowColor = Shader.PropertyToID("_ShallowColor");
        private static readonly int PropDeepColor = Shader.PropertyToID("_DeepColor");
        private static readonly int PropWallColor = Shader.PropertyToID("_WallColor");
        private static readonly int PropContourInterval = Shader.PropertyToID("_ContourInterval");
        private static readonly int PropBasinBottomDepth = Shader.PropertyToID("_BasinBottomDepth");
        private static readonly int PropHeightExaggeration = Shader.PropertyToID("_HeightExaggeration");
        #endregion
    }
}
