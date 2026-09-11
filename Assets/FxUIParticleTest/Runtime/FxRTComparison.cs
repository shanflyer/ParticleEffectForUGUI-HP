using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Coffee.UIExtensions;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FxUIParticleTest
{
    // Twenty effects in twenty dedicated effect Canvases; two copies of ten effect definitions (98 systems).
    public sealed class FxRTComparison : MonoBehaviour
    {
        public Shader particleShader;
        public Shader compositeShader;
        public int rtSize = 256;
        public float warmupSeconds = 5, sampleSeconds = 30;
        public bool optimizedUI; // Legacy serialized merge setting.
        public bool groupCache, bake30, staticCache, meshSharing;
        [Range(0, 2)] public int earlyCull;
        [Range(0,100)] public int groupCount = 20;
        public bool detailedTimings;
        public bool gammaOptimization;
        IDisposable timingRequest;
        Transform canvasRoot;
        int previousTargetFPS, previousVSync;
        Button lessGroups, moreGroups;
        Text groupsLabel;
        float layoutScale = 1;
        double lastSampleTime;
        const int ExtraStart = 14;
        static readonly int[] Resolutions = {128, 256, 512, 1024, 2048};
        readonly List<Button> optimizationButtons = new List<Button>();
        readonly List<GameObject> roots = new List<GameObject>();
        readonly List<Camera> cameras = new List<Camera>();
        readonly List<RenderTexture> textures = new List<RenderTexture>();
        readonly List<ParticleSystem> systems = new List<ParticleSystem>();
        readonly List<UIParticle> uiParticles = new List<UIParticle>();
        readonly List<RawImage> views = new List<RawImage>();
        readonly List<RectTransform> slots = new List<RectTransform>();
        readonly List<double[]> rows = new List<double[]>(36000);
        readonly double[][] sampleBuffer = new double[36000][];
        bool previousMerge, previousCache, previousStatic;
        int previousCull;
        int previousBake;
        readonly List<string> summaries = new List<string>();
        readonly FrameTiming[] timing = new FrameTiming[1];
        ProfilerRecorder[] counters;
        static readonly string[] Names = { "Main Thread", "Render Thread", "GC Allocated In Frame", "Total Used Memory", "Total Reserved Memory", "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Triangles Count", "Vertices Count" };
        static readonly string Header = "frame_ms,main_ms,render_ms,gc_bytes,used_bytes,reserved_bytes,draws,batches,setpass,triangles,vertices,gpu_ms,alive,rt_estimated_bytes";
        static readonly string[] ExtraNames = { "CPU Main Thread Active Time", "CPU Main Thread Frame Time (excluding vsync)", "GC Used Memory", "GC Reserved Memory", "Texture Memory", "Mesh Memory" };
        ProfilerRecorder[] extraCounters;
        static readonly string FullHeader = Header + ",cpu_active_ms,cpu_excluding_vsync_ms,gc_used_bytes,gc_reserved_bytes,texture_bytes,mesh_bytes,wall_frame_ms,ft_cpu_main_ms,ft_present_wait_ms,ft_render_ms,ui_prepare_ms,ui_simulate_ms,ui_bake_ms,ui_combine_ms,ui_submit_ms,ui_bake_attempts,ui_baked_vertices,ui_setmesh,ui_renderers,groups,systems,rt_count,ui_sample_frame";
        static readonly int ColumnCount = FullHeader.Split(',').Length;
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        Material material, composite;
        Text status, modeTitle;
        Button rtButton, uiButton, resolutionButton, optimizationButton;
        readonly List<Button> configurationButtons = new List<Button>();
        readonly Color rtColor = new Color(.12f,.55f,.9f), uiColor = new Color(.12f,.7f,.42f);
        static readonly Color IdleButtonColor = new Color(.2f,.24f,.3f);
        Font font;
        bool rtMode, recording, runningPair;
        float begin, nextLabel;
        string outputDirectory;
        Coroutine pair;
        void Start()
        {
            previousMerge=UIParticle.mergeRenderers;previousCache=UIParticle.useGroupCache;previousStatic=UIParticle.staticMeshCache;previousCull=UIParticle.earlyCull;previousBake=UIParticle.bakeFPS;
            for(int i=0;i<sampleBuffer.Length;i++)sampleBuffer[i]=new double[ColumnCount];
            previousTargetFPS=Application.targetFrameRate;previousVSync=QualitySettings.vSyncCount;
            QualitySettings.vSyncCount=0;Application.targetFrameRate = 200;
            groupCount=Mathf.Clamp(groupCount,0,100);
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Noto Sans SC", "Droid Sans Fallback", "Arial Unicode MS" }, 27);
            if (particleShader == null) { Debug.LogError("Comparison particle shader is missing."); enabled = false; return; }
            material = new Material(particleShader);
            composite = new Material(compositeShader);
            var screenCamera = new GameObject("ScreenCamera", typeof(Camera));
            screenCamera.transform.SetParent(transform,false);
            var screen = screenCamera.GetComponent<Camera>();screen.cullingMask=0;screen.clearFlags=CameraClearFlags.SolidColor;screen.backgroundColor=new Color(.02f,.025f,.035f,1);screen.depth=100;screen.allowHDR=false;screen.allowMSAA=false;
            var canvas = new GameObject("ComparisonCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.transform.SetParent(transform, false);canvasRoot=canvas.transform;
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1080,1920);
            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule)); events.transform.SetParent(transform,false);
            modeTitle = Label(canvas.transform, "CurrentMode", new Vector2(0,-48), new Vector2(1040,72), "");
            modeTitle.fontSize = 42;
            modeTitle.fontStyle = FontStyle.Bold;
            status = Label(canvas.transform, "Status", new Vector2(0,-172), new Vector2(1040,164), "");
            status.fontSize = 22;
            rtButton = Button(canvas.transform, "RT 模式", -390, -295, ()=>Switch(false,true));
            uiButton = Button(canvas.transform, "UI 粒子模式", -130, -295, ()=>Switch(false,false));
            Button(canvas.transform, "开始采集", 130, -295, StartRecording);
            Button(canvas.transform, "停止并保存", 390, -295, StopAll);
            configurationButtons.Add(Button(canvas.transform, "对比：RT → UI", -390, -390, ()=>RunPair(true)));
            configurationButtons.Add(Button(canvas.transform, "对比：UI → RT", -130, -390, ()=>RunPair(false)));
            resolutionButton = Button(canvas.transform, "RT 分辨率", 130, -390, ()=>{if(recording||runningPair)return;rtSize=Resolutions[(Array.IndexOf(Resolutions,rtSize)+1)%Resolutions.Length];Switch(false,rtMode);});
            optimizationButton = Button(canvas.transform, "UI 优化开关", 390, -390, ()=>{if(recording||runningPair)return;optimizedUI=!optimizedUI;Switch(false,rtMode);});
            AddOptimizationButton(canvas.transform, "组缓存", -390, -475, ()=>groupCache=!groupCache);
            AddOptimizationButton(canvas.transform, "烘焙降频", -130, -475, ()=>bake30=!bake30);
            AddOptimizationButton(canvas.transform, "裁剪", 130, -475, ()=>earlyCull=(earlyCull+1)%3);
            AddOptimizationButton(canvas.transform, "静态缓存", 390, -475, ()=>staticCache=!staticCache);
            AddOptimizationButton(canvas.transform, "网格共享", -390, -560, ()=>meshSharing=!meshSharing);
            AddOptimizationButton(canvas.transform,"顶点色 Gamma",-130,-560,()=>gammaOptimization=!gammaOptimization);
            Label(canvas.transform,"OptimizationHint",new Vector2(260,-560),new Vector2(500,70),"降频改变更新频率；静态缓存仅暂停时生效\n裁剪仅不可见时生效；共享复用相同特效").fontSize=22;
            lessGroups=Button(canvas.transform,"分组 -1",-390,-645,()=>ChangeGroups(-1));
            moreGroups=Button(canvas.transform,"分组 +1",-130,-645,()=>ChangeGroups(1));
            groupsLabel=Label(canvas.transform,"GroupCount",new Vector2(130,-645),new Vector2(240,72),"");
            groupsLabel.fontSize=22;
            configurationButtons.Add(Button(canvas.transform,"详细计时 开/关",390,-645,()=>{if(recording||runningPair)return;detailedTimings=!detailedTimings;RefreshStatus();}));
            BuildSlots();
            counters=new ProfilerRecorder[Names.Length];
            for(int i=0;i<Names.Length;i++)try{counters[i]=ProfilerRecorder.StartNew(i<2?ProfilerCategory.Internal:i<5?ProfilerCategory.Memory:ProfilerCategory.Render,Names[i],1);}catch(Exception){ }
            extraCounters=new ProfilerRecorder[ExtraNames.Length];
            for(int i=0;i<ExtraNames.Length;i++)try{extraCounters[i]=ProfilerRecorder.StartNew(i<2?ProfilerCategory.Internal:ProfilerCategory.Memory,ExtraNames[i],1);}catch(Exception){}
            outputDirectory=Path.Combine(Application.persistentDataPath,"FxRTComparison",DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory,"environment.txt"),"device="+SystemInfo.deviceModel+"\ngpu="+SystemInfo.graphicsDeviceName+"\napi="+SystemInfo.graphicsDeviceType+"\nunity="+Application.unityVersion+"\nscreen="+Screen.width+"x"+Screen.height+"\nvSync="+QualitySettings.vSyncCount+"\ntargetFPS="+Application.targetFrameRate+"\nimplementation=rt-comparison-v2-groups\nlayout=sandwich-per-canvas\nMissing counters are NaN. GPU timing requires platform support and Frame Timing Stats. RT estimate = ARGB32 color + 24bit depth rounded to 4 bytes, excludes driver overhead.\n");
            Switch(false,true);
        }
        void ChangeGroups(int delta)
        {
            if(recording||runningPair)return;
            int value=Mathf.Clamp(groupCount+delta,0,100);
            if(value==groupCount)return;
            ReleaseEffects();groupCount=value;BuildSlots();Switch(false,rtMode);
        }
        void BuildSlots()
        {
            foreach(var slot in slots)if(slot){var root=slot.parent.gameObject;root.SetActive(false);Destroy(root);}
            slots.Clear();
            int columns=Mathf.Max(4,Mathf.CeilToInt(Mathf.Sqrt(groupCount*1.0f)));
            int rowCount=Mathf.Max(1,Mathf.CeilToInt(groupCount/(float)columns));
            layoutScale=Mathf.Min(1,Mathf.Min(1040f/(columns*260),1210f/(rowCount*245)));
            for(int i=0;i<groupCount;i++)
            {
                var slot = new GameObject("特效画布_"+(i+1),typeof(RectTransform),typeof(Canvas),typeof(CanvasGroup));
                slot.transform.SetParent(canvasRoot,false);
                var rect=(RectTransform)slot.transform;
                rect.anchorMin=rect.anchorMax=new Vector2(.5f,1);
                rect.sizeDelta=new Vector2(250,230);
                rect.localScale=Vector3.one*layoutScale;
                rect.anchoredPosition=new Vector2((i%columns-(columns-1)*.5f)*260*layoutScale,-690-((i/columns)+.5f)*245*layoutScale);
                // Sibling order is stable across mode switches: background, particles, foreground.
                var bottom = Layer(rect, "01_底部UI");
                Panel(bottom, "底板", Vector2.zero, new Vector2(250,230), new Color(.04f,.05f,.07f,1));
                Panel(bottom, "底部装饰", new Vector2(0,-12), new Vector2(150,110), new Color(.10f,.16f,.22f,1));
                var particleLayer = Layer(rect, "02_粒子层");
                slots.Add(particleLayer);
                var top = Layer(rect, "03_顶部UI");
                Label(top,"标题",new Vector2(0,-22),new Vector2(246,40),"画布 "+(i+1)+" | "+(4+(i%10)%3)+" 个子系统").fontSize=22;
                // Cross the particle footprint so foreground occlusion is visible in both modes.
                var bar = Panel(top, "前景遮挡条", new Vector2(0,-38), new Vector2(180,26), new Color(.18f,.24f,.32f,1));
                Label(bar,"遮挡说明",new Vector2(0,-13),new Vector2(176,26),"顶部 UI · 遮挡粒子").fontSize=17;
                Panel(top, "前景徽标", new Vector2(55,18), new Vector2(30,30), new Color(.95f,.65f,.18f,1));
            }
        }
        RectTransform Layer(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }
        RectTransform Panel(Transform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = position; rect.sizeDelta = size;
            var graphic = go.GetComponent<Image>(); graphic.color = color; graphic.raycastTarget = false;
            return rect;
        }
        void AddOptimizationButton(Transform parent,string name,float x,float y,UnityEngine.Events.UnityAction change)
        {
            optimizationButtons.Add(Button(parent,name,x,y,()=>{if(recording||runningPair)return;change();Switch(false,rtMode);}));
        }
        Button Button(Transform parent,string title,float x,float y,UnityEngine.Events.UnityAction action)
        {
            var go=new GameObject(title,typeof(RectTransform),typeof(Image),typeof(Button));go.transform.SetParent(parent,false);
            var r=(RectTransform)go.transform;r.anchorMin=r.anchorMax=new Vector2(.5f,1);r.anchoredPosition=new Vector2(x,y);r.sizeDelta=new Vector2(245,76);go.GetComponent<Image>().color=new Color(.2f,.24f,.3f);
            go.GetComponent<Button>().onClick.AddListener(action);Label(go.transform,"Text",new Vector2(0,-38),new Vector2(240,72),title).fontSize=23;
            return go.GetComponent<Button>();
        }
        Text Label(Transform parent,string name,Vector2 pos,Vector2 size,string value)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Text));go.transform.SetParent(parent,false);var r=(RectTransform)go.transform;r.anchorMin=r.anchorMax=new Vector2(.5f,1);r.anchoredPosition=pos;r.sizeDelta=size;
            var text=go.GetComponent<Text>();text.font=font;text.fontSize=27;text.alignment=TextAnchor.MiddleCenter;text.color=Color.white;text.text=value;text.raycastTarget=false;return text;
        }
        void Switch(bool automatic,bool useRT)
        {
            if(!automatic&&(recording||runningPair))return;
            ReleaseEffects();rtMode=useRT;
            // Apply to the root AND each nested effect canvas used by renderer.canvas.
            canvasRoot.GetComponent<Canvas>().vertexColorAlwaysGammaSpace=!useRT&&gammaOptimization;
            foreach(var slot in slots)slot.parent.GetComponent<Canvas>().vertexColorAlwaysGammaSpace=!useRT&&gammaOptimization;
            UIParticle.mergeRenderers=optimizedUI;UIParticle.useGroupCache=groupCache;UIParticle.bakeFPS=bake30?30:0;UIParticle.staticMeshCache=staticCache;UIParticle.earlyCull=earlyCull;
            for(int i=0;i<groupCount;i++)
            {
                var root=new GameObject("Effect_"+i,typeof(RectTransform));roots.Add(root);
                if(useRT){root.transform.SetParent(transform,false);root.transform.position=new Vector3(10000+i*100,0,0);}
                else {root.transform.SetParent(slots[i],false);((RectTransform)root.transform).anchoredPosition=new Vector2(0,-12);}
                for(int j=0;j<4+(i%10)%3;j++)
                {
                    var child=new GameObject("PS_"+j);child.transform.SetParent(root.transform,false);child.layer=30;
                    var ps=child.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                    var m=ps.main;m.playOnAwake=false;m.loop=true;m.duration=3;m.startLifetime=1.8f;m.startSpeed=.55f+j*.07f;m.startSize=.1f+j*.018f;m.maxParticles=256;m.simulationSpace=ParticleSystemSimulationSpace.Local;m.startColor=Color.HSVToRGB(((i%10)*.1f+j*.06f)%1,.8f,1);
                    var e=ps.emission;e.rateOverTime=24+j*8;var shape=ps.shape;shape.shapeType=ParticleSystemShapeType.Circle;shape.radius=.1f;
                    ps.useAutoRandomSeed=false;ps.randomSeed=(uint)(1000+(i%10)*10+j);ps.GetComponent<ParticleSystemRenderer>().sharedMaterial=material;systems.Add(ps);
                }
                if(useRT)
                {
                    var tex=new RenderTexture(rtSize,rtSize,24,RenderTextureFormat.ARGB32){name="EffectRT_"+i,antiAliasing=1,useMipMap=false};tex.Create();textures.Add(tex);
                    var cameraGo=new GameObject("RTCamera_"+i,typeof(Camera));cameraGo.transform.SetParent(root.transform,false);cameraGo.transform.localPosition=new Vector3(0,0,-10);
                    var cam=cameraGo.GetComponent<Camera>();cam.orthographic=true;cam.orthographicSize=2;cam.aspect=1;cam.nearClipPlane=.1f;cam.farClipPlane=20;cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.clear;cam.cullingMask=1<<30;cam.allowHDR=false;cam.allowMSAA=false;cam.targetTexture=tex;cameras.Add(cam);
                    var view=new GameObject("RTView",typeof(RectTransform),typeof(RawImage));view.transform.SetParent(slots[i],false);((RectTransform)view.transform).sizeDelta=new Vector2(136,136);((RectTransform)view.transform).anchoredPosition=new Vector2(0,-12);var raw=view.GetComponent<RawImage>();raw.texture=tex;raw.material=composite;raw.raycastTarget=false;views.Add(raw);
                }
                else {var ui=root.AddComponent<UIParticle>();ui.scale=34;ui.groupMaxId=i%10+1;ui.groupId=i%10+1;ui.meshSharing=meshSharing?UIParticle.MeshSharing.Auto:UIParticle.MeshSharing.None;ui.RefreshParticles();ui.Play();uiParticles.Add(ui);}
            }
            if(useRT)foreach(var ps in systems)ps.Play(false);
            RefreshStatus();
        }
        void ReleaseEffects()
        {
            foreach(var c in cameras)if(c){c.enabled=false;c.targetTexture=null;}cameras.Clear();
            foreach(var v in views)if(v){v.texture=null;Destroy(v.gameObject);}views.Clear();
            foreach(var r in roots)if(r){r.SetActive(false);Destroy(r);}roots.Clear();systems.Clear();uiParticles.Clear();
            foreach(var t in textures)if(t){t.Release();Destroy(t);}textures.Clear();
        }
        void StartRecording(){if(recording||runningPair)return;Switch(false,rtMode);Begin();}
        void Begin(){timingRequest?.Dispose();timingRequest=detailedTimings?UIParticleProfiler.BeginDetailedTiming():null;rows.Clear();lastSampleTime=Time.realtimeSinceStartupAsDouble;begin=Time.unscaledTime;recording=true;RefreshStatus();}
        void RunPair(bool firstRT){if(recording||runningPair)return;pair=StartCoroutine(Pair(firstRT));}
        IEnumerator Pair(bool firstRT)
        {
            runningPair=true;Switch(true,firstRT);yield return null;Begin();while(recording)yield return null;
            Switch(true,!firstRT);yield return null;Begin();while(recording)yield return null;runningPair=false;pair=null;RefreshStatus();
        }
        void StopAll(){if(pair!=null)StopCoroutine(pair);pair=null;runningPair=false;Save();RefreshStatus();}
        void Update()
        {
            FrameTimingManager.CaptureFrameTimings();
            if(recording&&Time.unscaledTime-begin>=warmupSeconds+sampleSeconds)Save();
            if(Time.unscaledTime<nextLabel)return;nextLabel=Time.unscaledTime+.5f;
            int alive=0;foreach(var p in systems)if(p)alive+=p.particleCount;
            RefreshStatus(alive);
        }
        void RefreshStatus(int alive = -1)
        {
            if (status == null) return;
            if (alive < 0) { alive = 0; foreach (var ps in systems) if (ps) alive += ps.particleCount; }
            modeTitle.text = rtMode ? "当前模式：RT 渲染" : "当前模式：UI 粒子";
            modeTitle.color = rtMode ? rtColor : uiColor;
            var resolution = rtMode
                ? $"RT：{textures.Count} 张 · {rtSize}×{rtSize} 像素 | ARGB32 | 深度24位 | 抗锯齿关"
                : "RT：未使用 | UI 粒子直接绘制到画布";
            var optimization = rtMode ? $"合并：不适用（预设： {(optimizedUI ? "开" : "关")}）"
                : $"网格合并： {(optimizedUI ? "开" : "关")} | 烘焙：{(bake30 ? "30Hz" : "每帧")}";
            var progress = recording
                ? (Time.unscaledTime-begin < warmupSeconds ? "预热中" : "采集中") + $" {Time.unscaledTime-begin:F1}/{warmupSeconds+sampleSeconds:F0} 秒"
                : "待机 | 已保存组数：" + summaries.Count;
            status.text = resolution + $"\n屏幕：{Screen.width}×{Screen.height} 像素 | 目标：{Application.targetFrameRate} FPS | 垂直同步：{QualitySettings.vSyncCount}"
                + $"\n特效画布：{slots.Count} | 系统：{systems.Count} | 存活粒子：{alive} | {optimization}"
                + "\n" + progress;
            rtButton.GetComponent<Image>().color = rtMode ? rtColor : IdleButtonColor;
            uiButton.GetComponent<Image>().color = rtMode ? IdleButtonColor : uiColor;
            rtButton.GetComponentInChildren<Text>().text = rtMode ? "RT【当前】" : "切换到 RT";
            uiButton.GetComponentInChildren<Text>().text = rtMode ? "切换到 UI 粒子" : "UI 粒子【当前】";
            resolutionButton.GetComponentInChildren<Text>().text = $"RT {rtSize}x{rtSize}" + (rtMode ? "\n切换分辨率" : "\nRT 模式预设");
            optimizationButton.GetComponentInChildren<Text>().text = "网格合并：" + (optimizedUI ? "开" : "关") + (rtMode ? "\nUI 模式预设" : "\n点击切换");
            string[] labels = {"组缓存："+(groupCache?"开":"关"),"烘焙："+(bake30?"30Hz":"每帧"),"裁剪："+(earlyCull==0?"关":earlyCull==1?"仅渲染":"渲染+模拟"),"静态缓存："+(staticCache?"开":"关"),"网格共享："+(meshSharing?"开":"关"),"顶点色 Gamma："+(gammaOptimization?"开":"关")};
            for(int i=0;i<optimizationButtons.Count;i++)optimizationButtons[i].GetComponentInChildren<Text>().text=labels[i]+(rtMode?"\nUI 模式预设":"");
            groupsLabel.text="分组："+groupCount+" / 100\n详细计时："+(detailedTimings?"开":"关");
            var unlocked = !recording && !runningPair;
            lessGroups.interactable=unlocked&&groupCount>0;moreGroups.interactable=unlocked&&groupCount<100;
            foreach(var button in optimizationButtons)button.interactable=unlocked;
            rtButton.interactable = unlocked; uiButton.interactable = unlocked;
            resolutionButton.interactable = unlocked; optimizationButton.interactable = unlocked;
            foreach (var button in configurationButtons) button.interactable = unlocked;
        }
        void LateUpdate()
        {
            double now=Time.realtimeSinceStartupAsDouble;
            double wallMs=(now-lastSampleTime)*1000;lastSampleTime=now;
            if(!recording||Time.unscaledTime-begin<warmupSeconds)return;
            var row=sampleBuffer[rows.Count];row[12]=0;row[0]=Time.unscaledDeltaTime*1000;
            for(int i=0;i<counters.Length;i++)row[i+1]=counters[i].Valid&&counters[i].Count>0?counters[i].LastValue*(i<2?1e-6:1):double.NaN;
            row[11]=FrameTimingManager.GetLatestTimings(1,timing)>0&&timing[0].gpuFrameTime>0?timing[0].gpuFrameTime:double.NaN;
            foreach(var ps in systems)if(ps)row[12]+=ps.particleCount;row[13]=rtMode?(long)textures.Count*rtSize*rtSize*8:0;
            for(int i=0;i<extraCounters.Length;i++)row[ExtraStart+i]=extraCounters[i].Valid&&extraCounters[i].Count>0?extraCounters[i].LastValue*(i<2?1e-6:1):double.NaN;
            row[20]=wallMs;
            bool validTiming=FrameTimingManager.GetLatestTimings(1,timing)>0&&timing[0].cpuMainThreadFrameTime>0;
            row[21]=validTiming?timing[0].cpuMainThreadFrameTime:double.NaN;
            row[22]=validTiming?timing[0].cpuMainThreadPresentWaitTime:double.NaN;
            row[23]=validTiming?timing[0].cpuRenderThreadFrameTime:double.NaN;
            var u=UIParticleProfiler.completed;
            bool validUI=!rtMode && Time.frameCount-u.frame<=1 && u.frame>0;
            row[24]=validUI&&u.detailedTiming?u.prepareMs:double.NaN;
            row[25]=validUI&&u.detailedTiming?u.simulateMs:double.NaN;
            row[26]=validUI&&u.detailedTiming?u.bakeMs:double.NaN;
            row[27]=validUI&&u.detailedTiming?u.combineMs:double.NaN;
            row[28]=validUI&&u.detailedTiming?u.submitMs:double.NaN;
            row[29]=validUI?u.bakeOps:double.NaN;row[30]=validUI?u.bakedVertices:double.NaN;
            row[31]=validUI?u.setMeshOps:double.NaN;row[32]=validUI?u.activeRenderers:double.NaN;
            row[33]=roots.Count;row[34]=systems.Count;row[35]=textures.Count;row[36]=validUI?u.frame:double.NaN;
            rows.Add(row);
            if(rows.Count>=36000)Save();
        }
        void Save()
        {
            if(!recording)return;recording=false;timingRequest?.Dispose();timingRequest=null;
            var id=DateTime.Now.ToString("HHmmss_fff")+"_"+(rtMode?"RT":"UI")+"_g"+groupCount+"_rt"+rtSize+"_merge"+optimizedUI+"_cache"+groupCache+"_bake30"+bake30+"_cull"+earlyCull+"_static"+staticCache+"_sharing"+meshSharing+"_detail"+detailedTimings+"_gamma"+gammaOptimization;
            File.WriteAllText(Path.Combine(outputDirectory,id+".meta.txt"),"groups="+roots.Count+"\nsystems="+systems.Count+"\nlayoutScale="+layoutScale.ToString("R",CI)+"\ntargetFPS="+Application.targetFrameRate+"\nvSync="+QualitySettings.vSyncCount+"\nscreen="+Screen.width+"x"+Screen.height+"\ngammaOptimization="+gammaOptimization+"\ncolorSpace="+QualitySettings.activeColorSpace+"\ndetailedTimings="+detailedTimings+"\n");
            var csv=new StringBuilder(FullHeader+"\n");foreach(var row in rows)csv.AppendLine(string.Join(",",Array.ConvertAll(row,v=>v.ToString("R",CI))));File.WriteAllText(Path.Combine(outputDirectory,id+".csv"),csv.ToString());
            var summary=new StringBuilder();for(int c=0;c<ColumnCount;c++)
            {
                var values=new List<double>();foreach(var row in rows)if(!double.IsNaN(row[c]))values.Add(row[c]);values.Sort();double sum=0;foreach(var v in values)sum+=v;
                summary.AppendLine(id+","+FullHeader.Split(',')[c]+","+values.Count+","+(values.Count>0?(sum/values.Count).ToString("R",CI):"NaN")+","+(values.Count>0?values[(int)Math.Ceiling(values.Count*.95)-1].ToString("R",CI):"NaN"));
            }
            summaries.Add(summary.ToString());File.WriteAllText(Path.Combine(outputDirectory,"comparison.csv"),"run,metric,valid_samples,mean,p95\n"+string.Concat(summaries));Debug.Log("RT 对比数据已保存："+outputDirectory);RefreshStatus();
        }
        void OnDisable(){Application.targetFrameRate=previousTargetFPS;QualitySettings.vSyncCount=previousVSync;UIParticle.mergeRenderers=previousMerge;UIParticle.useGroupCache=previousCache;UIParticle.staticMeshCache=previousStatic;UIParticle.earlyCull=previousCull;UIParticle.bakeFPS=previousBake;StopAll();ReleaseEffects();if(extraCounters!=null)for(int i=0;i<extraCounters.Length;i++)extraCounters[i].Dispose();extraCounters=null;if(counters!=null)for(int i=0;i<counters.Length;i++)counters[i].Dispose();}
        void OnDestroy(){timingRequest?.Dispose();if(extraCounters!=null)for(int i=0;i<extraCounters.Length;i++)extraCounters[i].Dispose();if(material)Destroy(material);if(composite)Destroy(composite);}
    }
}

