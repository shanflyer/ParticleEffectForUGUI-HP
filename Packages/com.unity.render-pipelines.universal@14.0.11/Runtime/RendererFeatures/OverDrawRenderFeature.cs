using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class OverDrawRenderFeature : ScriptableRendererFeature
{
    public static bool OverDrawEnabled = false;

    private OverDrawRenderPass pass;
    public ComputeShader OverdrawComputeShader;
    public Material OverdrawMaterial;
    [Range(1, 255)] public int MaxOverdraw = 20;

    private UniversalAdditionalCameraData cameraData;

    public override void Create()
    {
#if UNITY_EDITOR
        pass?.Dispose();
        pass = new OverDrawRenderPass();
        pass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents + 10;
#endif
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
#if UNITY_EDITOR
        if (!OverDrawEnabled || !SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback
            || pass == null || OverdrawComputeShader == null || OverdrawMaterial == null
            || OverdrawMaterial.passCount < 9 || !OverdrawComputeShader.HasKernel("OverdrawComputeShader"))
        {
            return;
        }

        if (renderingData.cameraData.isPreviewCamera || renderingData.cameraData.isSceneViewCamera)
            return;

        // This diagnostic shader reads Texture2D. MSAA/array targets require a
        // different resolve path and cannot be bound to its current compute kernel.
        var descriptor = renderingData.cameraData.cameraTargetDescriptor;
        if (descriptor.msaaSamples > 1 || descriptor.dimension != TextureDimension.Tex2D) return;

        renderingData.cameraData.camera.TryGetComponent(out cameraData);

        pass.Setup(ref renderingData, OverdrawComputeShader, OverdrawMaterial, cameraData, MaxOverdraw);
        renderer.EnqueuePass(pass);
#endif

    }
    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        pass = null;
    }

    private class OverDrawRenderPass : ScriptableRenderPass
    {
        const string m_ProfilerTag = "OverDrawRenderPass";
        private static readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
        private const int OVER_DRAW_ARRAY_LENGTH = 30;

        private FilteringSettings m_FilteringTransparentSettings = new FilteringSettings(new RenderQueueRange(2501, 5000));
        //private FilteringSettings m_FilteringOpaqueSettings = new FilteringSettings(new RenderQueueRange(0, 2449));
        private static readonly int _MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int _OverdrawArray = Shader.PropertyToID("_OverdrawArray");
        private static readonly int _OverdrawBitValue = Shader.PropertyToID("_OverdrawBitValue");
        private static readonly int _OverdrawDisplayMax = Shader.PropertyToID("_OverdrawDisplayMax");

        private ComputeShader computeShader;
        private Material overdrawMaterial;
        private RTHandle m_OverdrawAttachment;
        private List<ShaderTagId> m_ShaderOpaqueTags;
        private List<ShaderTagId> m_ShaderTransparentTags;
        private OverdrawCounter[] overDrawCounterArray = null;
        private int overDrawArrayIndex;
        private int lastOverDrawArrayIndex = -1;
        private long lastCompletedSequence = -1;
        private long nextSequence;
        private UniversalAdditionalCameraData cameraData;
        private int maxOverdraw;
        private RenderStateBlock overdrawStateBlock;

        public OverDrawRenderPass()
        {
            m_ShaderTransparentTags = new List<ShaderTagId>();
            m_ShaderTransparentTags.Add(new ShaderTagId("UniversalForward"));
            m_ShaderTransparentTags.Add(new ShaderTagId("UniversalForwardOnly"));
            m_ShaderTransparentTags.Add(new ShaderTagId("BackGround"));
            m_ShaderTransparentTags.Add(new ShaderTagId("ShadowFake"));
            m_ShaderTransparentTags.Add(new ShaderTagId("OutLine"));
            m_ShaderTransparentTags.Add(new ShaderTagId("SRPDefaultUnlit"));
            m_ShaderTransparentTags.Add(new ShaderTagId(""));

            m_ShaderOpaqueTags = new List<ShaderTagId>();
            m_ShaderOpaqueTags.Add(new ShaderTagId("AirDistortion"));

            var noColorWrite = new RenderTargetBlendState(writeMask: (ColorWriteMask)0);
            var stencilState = new StencilState(
                enabled: true,
                readMask: byte.MaxValue,
                writeMask: byte.MaxValue,
                compareFunctionFront: CompareFunction.Always,
                passOperationFront: StencilOp.IncrementSaturate,
                failOperationFront: StencilOp.Keep,
                zFailOperationFront: StencilOp.Keep,
                compareFunctionBack: CompareFunction.Always,
                passOperationBack: StencilOp.IncrementSaturate,
                failOperationBack: StencilOp.Keep,
                zFailOperationBack: StencilOp.Keep);

            overdrawStateBlock = new RenderStateBlock(RenderStateMask.Blend | RenderStateMask.Depth | RenderStateMask.Stencil)
            {
                blendState = new BlendState { blendState0 = noColorWrite },
                depthState = new DepthState(false, CompareFunction.LessEqual),
                stencilReference = 0,
                stencilState = stencilState
            };
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ConfigureTarget(m_OverdrawAttachment, renderingData.cameraData.renderer.cameraDepthTargetHandle);
            ConfigureClear(ClearFlag.Color, Color.black);
        }

        public void Setup(ref RenderingData renderingData, ComputeShader computeShader, Material overdrawMaterial, UniversalAdditionalCameraData cameraData, int maxOverdraw)
        {
            this.computeShader = computeShader;
            this.overdrawMaterial = overdrawMaterial;
            this.cameraData = cameraData;
            this.maxOverdraw = Mathf.Clamp(maxOverdraw, 1, 255);

            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;

            RenderingUtils.ReAllocateIfNeeded(ref m_OverdrawAttachment, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_OverdrawTexture");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (overdrawMaterial != null)
            {
                if (overDrawCounterArray != null)
                {
                    //获取CS计算结果
                    for (int i = 0; i < overDrawCounterArray.Length; i++)
                    {
                        var _counter = overDrawCounterArray[i];
                        if (_counter != null)
                        {
                            if (!_counter.dirty && _counter.hasResult)
                            {
                                if (_counter.overDrawArray[0] > 0)
                                {
                                    _counter.dirty = false;
                                    _counter.overDraw = 0;

                                    // 只用实际发生绘制的像素作为分母，避免空白区域稀释结果。
                                    if (_counter.overDrawArray[1] > 0)
                                    {
                                        _counter.overDraw = Mathf.RoundToInt(100f * _counter.overDrawArray[2] / _counter.overDrawArray[1]);
                                    }

                                    if (_counter.sequence > lastCompletedSequence)
                                    {
                                        lastCompletedSequence = _counter.sequence;
                                        lastOverDrawArrayIndex = i;
                                    }
                                }
                            }
                        }
                    }

                    //统计结果
                    var curOverDraw = 0;
                    var avgOverDraw = 0;
                    var totalOverDraw = 0;
                    var totalCount = 0;

                    if (lastOverDrawArrayIndex >= 0)
                    {
                        curOverDraw = overDrawCounterArray[lastOverDrawArrayIndex].overDraw;

                        for (int i = 0; i < overDrawCounterArray.Length; i++)
                        {
                            var _counter = overDrawCounterArray[i];
                            if (_counter != null && !_counter.dirty && _counter.overDrawArray[1] > 0)
                            {
                                totalCount++;
                                totalOverDraw += _counter.overDraw;
                            }
                        }

                        if (totalCount > 0)
                        {
                            avgOverDraw = totalOverDraw / totalCount;
                        }
                    }

                    if (cameraData != null)
                    {
                        cameraData.CurOverdraw = curOverDraw;
                        cameraData.AvgOverdraw = avgOverDraw;
                    }
                }

                var cmd = CommandBufferPool.Get();
                OverdrawCounter pendingCounter = null;
                var submitted = false;
                try
                {
                
                    using (new ProfilingScope(cmd, m_ProfilingSampler))
                    {
                        cmd.ClearRenderTarget(RTClearFlags.Stencil, Color.black, 1.0f, 0);
                        context.ExecuteCommandBuffer(cmd);
                        cmd.Clear();
                        var drawTransparentMask = RenderingUtils.CreateDrawingSettings(m_ShaderTransparentTags, ref renderingData, SortingCriteria.CommonTransparent);
                        context.DrawRenderers(renderingData.cullResults, ref drawTransparentMask, ref m_FilteringTransparentSettings, ref overdrawStateBlock);
                        //context.DrawRenderers(renderingData.cullResults, ref drawOpaqueMask, ref m_FilteringOpaqueSettings);

                        for (int bit = 0; bit < 8; bit++)
                        {
                            cmd.SetGlobalFloat(_OverdrawBitValue, 1 << bit);
                            cmd.DrawProcedural(Matrix4x4.identity, overdrawMaterial, bit, MeshTopology.Triangles, 3, 1);
                        }
                    }

                    //ComputeBuffer
                    if (computeShader != null)
                    {
                        var overdrawCounter = GetNextOverdrawArray();
                        if (overdrawCounter != null)
                        {
                            pendingCounter = overdrawCounter;
                            overdrawCounter.dirty = true;
                            overdrawCounter.hasResult = false;
                            overdrawCounter.sequence = ++nextSequence;
                            overdrawCounter.Clear();

                            var overdrawKernel = computeShader.FindKernel("OverdrawComputeShader");
                            cmd.SetComputeTextureParam(computeShader, overdrawKernel, _MainTex, m_OverdrawAttachment.rt);
                            overdrawCounter.ComputeBuffer.SetData(overdrawCounter.overDrawArray);
                            cmd.SetComputeBufferParam(computeShader, overdrawKernel, _OverdrawArray, overdrawCounter.ComputeBuffer);
                            cmd.DispatchCompute(computeShader, overdrawKernel, Mathf.CeilToInt(m_OverdrawAttachment.rt.width / 8.0f), Mathf.CeilToInt(m_OverdrawAttachment.rt.height / 8.0f), 1);
                            // The callback owns the buffer until the GPU readback completes.
                            // Never block the editor thread with ComputeBuffer.GetData.
                            cmd.RequestAsyncReadback(overdrawCounter.ComputeBuffer, overdrawCounter.CompleteReadback);
                        }
                    }

                    cmd.SetGlobalFloat(_OverdrawDisplayMax, maxOverdraw);
                    Blitter.BlitCameraTexture(cmd, m_OverdrawAttachment, renderingData.cameraData.renderer.cameraColorTargetHandle, overdrawMaterial, 8);
                    context.ExecuteCommandBuffer(cmd);
                    submitted = true;

                }
                finally
                {
                    if (!submitted && pendingCounter != null) pendingCounter.CancelUnsubmitted();
                    CommandBufferPool.Release(cmd);
                }
            }
        }

        public void Dispose()
        {
            m_OverdrawAttachment?.Release();
            m_OverdrawAttachment = null;

            if (overDrawCounterArray != null)
            { 
                for (int i = 0; i < overDrawCounterArray.Length; i++)
                {
                    var _counter = overDrawCounterArray[i];
                    if (_counter != null)
                    {
                        _counter.Release();
                    }
                }

                overDrawCounterArray = null;
            }
        }

        private OverdrawCounter GetNextOverdrawArray()
        {
            if (overDrawCounterArray == null)
            {
                overDrawCounterArray = new OverdrawCounter[OVER_DRAW_ARRAY_LENGTH];
            }

            for (var i = 0; i < OVER_DRAW_ARRAY_LENGTH; i++)
            {
                overDrawArrayIndex = (++overDrawArrayIndex % OVER_DRAW_ARRAY_LENGTH);
                if (overDrawCounterArray[overDrawArrayIndex] == null)
                    overDrawCounterArray[overDrawArrayIndex] = new OverdrawCounter();
                if (!overDrawCounterArray[overDrawArrayIndex].dirty)
                    return overDrawCounterArray[overDrawArrayIndex];
            }
            // Backpressure: skip a sample instead of overwriting an in-flight buffer.
            return null;
        }
    }

    private class OverdrawCounter
    {
        ComputeBuffer computeBuffer = null;
        public ComputeBuffer ComputeBuffer
        {
            get
            {
                if (releaseRequested) throw new System.ObjectDisposedException(nameof(OverdrawCounter));
                if (computeBuffer == null)
                {
                    computeBuffer = new ComputeBuffer(4, sizeof(uint));
                }
                return computeBuffer;
            }
        }

        public uint[] overDrawArray = new uint[4] { 0, 0, 0, 0 };
        public bool dirty = false;
        public bool hasResult;
        private bool releaseRequested;
        public int overDraw = 0;
        public long sequence;

        public void Clear()
        {
            overDrawArray[0] = 0;
            overDrawArray[1] = 0;
            overDrawArray[2] = 0;
            overDrawArray[3] = 0;
        }

        public void Release()
        {
            releaseRequested = true;
            if (!dirty) ReleaseBuffer();
        }

        public void CancelUnsubmitted()
        {
            dirty = false;
            hasResult = false;
            if (releaseRequested) ReleaseBuffer();
        }

        public void CompleteReadback(AsyncGPUReadbackRequest request)
        {
            try
            {
                if (!releaseRequested && !request.hasError)
                {
                    var data = request.GetData<uint>();
                    if (data.Length >= overDrawArray.Length)
                    {
                        for (var i = 0; i < overDrawArray.Length; i++) overDrawArray[i] = data[i];
                        hasResult = true;
                    }
                }
            }
            finally
            {
                dirty = false;
                if (releaseRequested) ReleaseBuffer();
            }
        }

        private void ReleaseBuffer()
        {
            computeBuffer?.Release();
            computeBuffer = null;
        }
    }
}
