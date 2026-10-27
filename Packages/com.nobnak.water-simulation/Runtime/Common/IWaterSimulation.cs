using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Common interface for all water simulation methods.
    /// Provides access to simulation output textures, bounds, and lifecycle controls.
    /// </summary>
    public interface IWaterSimulation {
        /// <summary>Simulation area size in world units [m].</summary>
        Vector2 AreaSize { get; }

        /// <summary>Grid resolution (width, height) in cells.</summary>
        Vector2Int Resolution { get; }

        /// <summary>Current simulation elapsed time [s].</summary>
        float SimulationTime { get; }

        /// <summary>Reference water level (world Y) [m].</summary>
        float ReferenceWaterLevel { get; }

        /// <summary>
        /// Water displacement texture:
        /// R: Horizontal X displacement [m] (0 for heightfield-only)
        /// G: Vertical Y displacement (wave height eta) [m]
        /// B: Horizontal Z displacement [m] (0 for heightfield-only)
        /// A: Foam / Aux mask [0..1]
        /// </summary>
        RenderTexture WaterDataTexture { get; }

        /// <summary>Normal and foam texture (RGB: world normal, A: foam intensity).</summary>
        RenderTexture NormalTexture { get; }

        /// <summary>Optional bathymetry / water depth texture (R: water depth h [m], G: bed level [m]).</summary>
        RenderTexture BathymetryTexture { get; }

        /// <summary>Whether the simulation is currently stepping forward.</summary>
        bool IsRunning { get; }

        /// <summary>Start or resume simulation.</summary>
        void Play();

        /// <summary>Pause simulation.</summary>
        void Pause();

        /// <summary>Step simulation forward by a given delta time [s].</summary>
        void Step(float dt);

        /// <summary>Reset simulation state to reproducible initial condition.</summary>
        void ResetSimulation();
    }
}
