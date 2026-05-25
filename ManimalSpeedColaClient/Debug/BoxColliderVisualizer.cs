using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // draws the 12 edges of a BoxCollider's bounds in world space using
    // immediate-mode GL.LINES. visible through walls (ZTest = Always) so it
    // can be used to dial in interaction-area size without occlusion getting
    // in the way.
    //
    // requires the Hidden/Internal-Colored shader to be present in the build;
    // if Shader.Find returns null the component disables itself.
    public sealed class BoxColliderVisualizer : MonoBehaviour
    {
        public BoxCollider Target;
        public Color LineColor = new Color(0f, 1f, 0.3f, 1f);

        private Material _lineMaterial;

        private void Awake()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                Plugin.LogSource?.LogWarning("[SpeedCola] visualizer: Hidden/Internal-Colored shader not found - disabling.");
                enabled = false;
                return;
            }
            _lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _lineMaterial.SetInt("_ZWrite", 0);
            _lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private void OnRenderObject()
        {
            if (Target == null || _lineMaterial == null) return;

            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Target.transform.localToWorldMatrix);
            GL.Begin(GL.LINES);
            GL.Color(LineColor);
            DrawBoxEdges(Target.center, Target.size);
            GL.End();
            GL.PopMatrix();
        }

        private static void DrawBoxEdges(Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Vector3 a = center + new Vector3(-h.x, -h.y, -h.z);
            Vector3 b = center + new Vector3( h.x, -h.y, -h.z);
            Vector3 c = center + new Vector3( h.x, -h.y,  h.z);
            Vector3 d = center + new Vector3(-h.x, -h.y,  h.z);
            Vector3 e = center + new Vector3(-h.x,  h.y, -h.z);
            Vector3 f = center + new Vector3( h.x,  h.y, -h.z);
            Vector3 g = center + new Vector3( h.x,  h.y,  h.z);
            Vector3 i = center + new Vector3(-h.x,  h.y,  h.z);

            // bottom face
            Edge(a, b); Edge(b, c); Edge(c, d); Edge(d, a);
            // top face
            Edge(e, f); Edge(f, g); Edge(g, i); Edge(i, e);
            // verticals
            Edge(a, e); Edge(b, f); Edge(c, g); Edge(d, i);
        }

        private static void Edge(Vector3 from, Vector3 to)
        {
            GL.Vertex(from);
            GL.Vertex(to);
        }

        private void OnDestroy()
        {
            if (_lineMaterial != null) DestroyImmediate(_lineMaterial);
        }
    }
}
