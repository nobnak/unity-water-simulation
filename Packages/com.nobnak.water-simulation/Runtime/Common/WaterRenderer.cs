using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Renders water surface mesh displaced by IWaterSimulation outputs.
    /// Can connect to any simulation method implementing IWaterSimulation.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class WaterRenderer : MonoBehaviour {
        public enum VisualMode {
            Realistic = 0,
            HeightMap = 1,
            NormalVisual = 2,
            Wireframe = 3,
            Bathymetry = 4
        }

        #region serialized fields
        [Header("Target Simulation")]
        [Tooltip("The water simulation providing displacement data.")]
        [SerializeField] private WaterSimulationBase targetSimulation;

        [Header("Mesh Geometry")]
        [Tooltip("Mesh grid resolution along X and Z axes.")]
        [SerializeField] private Vector2Int meshResolution = new Vector2Int(256, 256);

        [Header("Visual Settings")]
        [SerializeField] private VisualMode visualizationMode = VisualMode.Realistic;
        [SerializeField] private Material waterMaterial;
        [SerializeField] private float heightMapRange = 2.0f;
        [SerializeField] private float displacementScale = 1.0f;
        [Range(0.5f, 4.0f)]
        [SerializeField] private float normalExaggeration = 2.0f;
        #endregion

        #region private fields
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh generatedMesh;
        private MaterialPropertyBlock propBlock;
        private Vector2 lastAreaSize;
        private Vector2Int lastMeshResolution;
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

        public float NormalExaggeration {
            get => normalExaggeration;
            set {
                normalExaggeration = math.clamp(value, 0.5f, 4.0f);
                UpdateMaterialProperties();
            }
        }

        public Material WaterMaterial {
            get => waterMaterial;
            set {
                waterMaterial = value;
                if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
                if (meshRenderer != null) meshRenderer.sharedMaterial = value;
            }
        }
        #endregion

        #region Unity lifecycle
        private void OnEnable() {
            meshFilter = GetComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            propBlock = new MaterialPropertyBlock();

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
            if (generatedMesh == null || areaSize != lastAreaSize || meshResolution != lastMeshResolution) {
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

            if (targetSimulation != null) {
                if (targetSimulation.WaterDataTexture != null) {
                    propBlock.SetTexture(PropWaterDataTex, targetSimulation.WaterDataTexture);
                }
                if (targetSimulation.NormalTexture != null) {
                    propBlock.SetTexture(PropNormalTex, targetSimulation.NormalTexture);
                }
                if (targetSimulation.BathymetryTexture != null) {
                    propBlock.SetTexture(PropBathymetryTex, targetSimulation.BathymetryTexture);
                    propBlock.SetFloat(PropHasBathymetry, 1.0f);
                } else {
                    propBlock.SetFloat(PropHasBathymetry, 0.0f);
                }
            } else {
                propBlock.SetFloat(PropHasBathymetry, 0.0f);
            }

            propBlock.SetFloat(PropVisualMode, (float)visualizationMode);
            propBlock.SetFloat(PropHeightMapRange, heightMapRange);
            propBlock.SetFloat(PropDisplacementScale, displacementScale);
            propBlock.SetFloat(PropNormalExaggeration, normalExaggeration);

            meshRenderer.SetPropertyBlock(propBlock);
        }
        #endregion

        #region private methods
        private void EnsureMesh() {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

            Vector2 size = targetSimulation != null ? targetSimulation.AreaSize : new Vector2(50f, 50f);
            int resX = math.clamp(meshResolution.x, 2, 512);
            int resY = math.clamp(meshResolution.y, 2, 512);

            if (generatedMesh != null) {
                DestroyImmediate(generatedMesh);
            }

            generatedMesh = GeneratePlaneMesh(size.x, size.y, resX, resY);
            meshFilter.sharedMesh = generatedMesh;

            lastAreaSize = size;
            lastMeshResolution = new Vector2Int(resX, resY);

            if (meshRenderer.sharedMaterial == null && waterMaterial != null) {
                meshRenderer.sharedMaterial = waterMaterial;
            }
        }

        private Mesh GeneratePlaneMesh(float width, float length, int resX, int resZ) {
            var mesh = new Mesh {
                name = "WaterPlaneMesh"
            };

            int vertCount = resX * resZ;
            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var tangents = new Vector4[vertCount];

            float dx = width / (resX - 1);
            float dz = length / (resZ - 1);
            float startX = -width * 0.5f;
            float startZ = -length * 0.5f;

            for (int z = 0; z < resZ; z++) {
                for (int x = 0; x < resX; x++) {
                    int i = z * resX + x;
                    vertices[i] = new Vector3(startX + x * dx, 0f, startZ + z * dz);
                    normals[i] = Vector3.up;
                    uvs[i] = new Vector2((float)x / (resX - 1), (float)z / (resZ - 1));
                    tangents[i] = new Vector4(1f, 0f, 0f, 1f);
                }
            }

            int indexCount = (resX - 1) * (resZ - 1) * 6;
            if (vertCount > 65535) {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            var indices = new int[indexCount];
            int idx = 0;
            for (int z = 0; z < resZ - 1; z++) {
                for (int x = 0; x < resX - 1; x++) {
                    int bl = z * resX + x;
                    int br = bl + 1;
                    int tl = bl + resX;
                    int tr = tl + 1;

                    indices[idx++] = bl;
                    indices[idx++] = tl;
                    indices[idx++] = br;

                    indices[idx++] = br;
                    indices[idx++] = tl;
                    indices[idx++] = tr;
                }
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.tangents = tangents;
            mesh.triangles = indices;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(width, 20f, length));
            return mesh;
        }
        #endregion

        #region constants / static
        private static readonly int PropWaterDataTex = Shader.PropertyToID("_WaterDataTex");
        private static readonly int PropNormalTex = Shader.PropertyToID("_NormalTex");
        private static readonly int PropBathymetryTex = Shader.PropertyToID("_BathymetryTex");
        private static readonly int PropHasBathymetry = Shader.PropertyToID("_HasBathymetry");
        private static readonly int PropVisualMode = Shader.PropertyToID("_VisualMode");
        private static readonly int PropHeightMapRange = Shader.PropertyToID("_HeightMapRange");
        private static readonly int PropDisplacementScale = Shader.PropertyToID("_DisplacementScale");
        private static readonly int PropNormalExaggeration = Shader.PropertyToID("_NormalExaggeration");
        #endregion
    }
}
