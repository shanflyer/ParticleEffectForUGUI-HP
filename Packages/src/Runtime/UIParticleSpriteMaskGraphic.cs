using Coffee.UIParticleInternal;
using UnityEngine;
using UnityEngine.UI;

namespace Coffee.UIExtensions
{
    [ExecuteAlways, AddComponentMenu("")]
    internal sealed class UIParticleSpriteMaskGraphic : Graphic
    {
        private static Shader s_Shader;
        internal static Shader shader => s_Shader ? s_Shader : (s_Shader = Resources.Load<Shader>("UIParticleSpriteMask"));
        private Mesh _mesh;
        private Material _drawMaterial;
        private Sprite _sprite;
        private Texture _texture;
        private Vector2[] _spriteVertices, _uv;
        private ushort[] _triangles;
        private Vector3[] _vertices;
        private int[] _indices;
        private Matrix4x4 _matrix;
        private bool _fullscreen;
        private bool _geometryValid;
        public override bool raycastTarget => false;

        internal static UIParticleSpriteMaskGraphic Create(UIParticle parent, string operation)
        {
            var go = new GameObject("[generated] SpriteMask " + operation, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(UIParticleSpriteMaskGraphic));
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent.transform, false);
            var graphic = go.GetComponent<UIParticleSpriteMaskGraphic>();
            graphic.canvasRenderer.cullTransparentMesh = false;
            return graphic;
        }

        internal void Configure(SpriteMask mask, Matrix4x4 matrix, int reference, int read, int write, bool fullscreen)
        {
            if (!_drawMaterial)
                _drawMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _drawMaterial.SetInt("_Stencil", reference);
            _drawMaterial.SetInt("_StencilReadMask", read);
            _drawMaterial.SetInt("_StencilWriteMask", write);
            _drawMaterial.SetFloat("_Fullscreen", fullscreen ? 1 : 0);
            _drawMaterial.SetFloat("_Cutoff", mask ? mask.alphaCutoff : 0);
            var sprite = mask ? mask.sprite : null;
            _drawMaterial.mainTexture = sprite ? sprite.texture : Texture2D.whiteTexture;
            _drawMaterial.SetTexture("_AlphaTex", sprite && sprite.associatedAlphaSplitTexture
                ? sprite.associatedAlphaSplitTexture : Texture2D.whiteTexture);
            _drawMaterial.SetFloat("_UseAlphaTex", sprite && sprite.associatedAlphaSplitTexture ? 1 : 0);
            var textureChanged = sprite && _texture != sprite.texture;
            if (!_geometryValid || _sprite != sprite || textureChanged || _matrix != matrix || _fullscreen != fullscreen)
            {
                if (!_mesh) _mesh = new Mesh { name = "UIParticle SpriteMask", hideFlags = HideFlags.HideAndDontSave };
                if (!_geometryValid || _sprite != sprite || textureChanged || _fullscreen != fullscreen)
                {
                    _spriteVertices = sprite ? sprite.vertices : new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                    _uv = sprite ? sprite.uv : _spriteVertices;
                    _triangles = sprite ? sprite.triangles : new ushort[] { 0, 1, 2, 2, 3, 0 };
                    _vertices = new Vector3[_spriteVertices.Length];
                    _indices = new int[_triangles.Length];
                    for (var i = 0; i < _indices.Length; i++) _indices[i] = _triangles[i];
                }
                for (var i = 0; i < _vertices.Length; i++)
                    _vertices[i] = fullscreen ? (Vector3)_spriteVertices[i] : matrix.MultiplyPoint3x4(_spriteVertices[i]);
                _mesh.Clear();
                _mesh.vertices = _vertices;
                _mesh.uv = _uv;
                _mesh.triangles = _indices;
                _mesh.RecalculateBounds();
                _sprite = sprite;
                _texture = sprite ? sprite.texture : null;
                _matrix = matrix;
                _fullscreen = fullscreen;
                _geometryValid = true;
            }
            // HP updates in willRenderCanvases, after the normal Graphic rebuild in some versions.
            // Submit here as well so animation, atlas UV and cutoff changes take effect this frame.
            Submit();
        }

        private void Submit()
        {
            if (!_mesh || !_drawMaterial) return;
            canvasRenderer.cull = false;
            canvasRenderer.cullTransparentMesh = false;
            canvasRenderer.SetMesh(_mesh);
            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(_drawMaterial, 0);
            canvasRenderer.SetTexture(_drawMaterial.mainTexture);
        }

        protected override void UpdateGeometry() { Submit(); }
        protected override void UpdateMaterial() { Submit(); }
        protected override void OnDestroy()
        {
            Misc.Destroy(_mesh);
            Misc.Destroy(_drawMaterial);
            base.OnDestroy();
        }
    }
}
