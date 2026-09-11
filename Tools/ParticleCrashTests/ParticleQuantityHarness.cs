using System;
using System.Collections.Generic;
using FxUIParticleTest;
namespace Coffee.UIExtensions
{
    public class UIParticle
    {
        public bool enabled = true;
        public readonly List<UnityEngine.ParticleSystem> particles = new List<UnityEngine.ParticleSystem>();
        public int plays, pauses;
        public void Play() { plays++; }
        public void Pause() { pauses++; }
        public void RefreshParticles(List<UnityEngine.ParticleSystem> value) { particles.Clear(); particles.AddRange(value); }
    }
}
namespace UnityEngine
{
    public class ParticleSystem
    {
        public class Main { public int maxParticles=200; }
        public class Emission
        {
            public bool enabled=true;
            public float rateOverTimeMultiplier=40,rateOverDistanceMultiplier=8;
            public Burst[] bursts={new Burst { count=new MinMaxCurve { constant=40 },time=2,cycleCount=3 }};
            public int burstCount=>bursts.Length;
            public void GetBursts(Burst[] target)=>Array.Copy(bursts,target,bursts.Length);
            public void SetBursts(Burst[] source)=>bursts=(Burst[])source.Clone();
        }
        public struct MinMaxCurve { public float constant; }
        public struct Burst { public MinMaxCurve count; public float time;public int cycleCount; }
        public Main main=new Main(); public Emission emission=new Emission();
    }
}
class ParticleQuantityHarness
{
    static int passed,failed;
    static void Assert(bool value){if(!value)throw new Exception("quantity contract failed");}
    static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
    static int Main()
    {
        Test("empty selection unregisters updates",()=>{
            var p=new Coffee.UIExtensions.UIParticle();var ps=new UnityEngine.ParticleSystem();p.particles.Add(ps);
            var setting=new ParticleUpdateSetting(p);var none=new HashSet<UnityEngine.ParticleSystem>();
            setting.Apply(none,false);setting.Apply(none,false);Assert(!p.enabled && p.plays==0);
        });
        Test("selected systems rebuild exact UIParticle list",()=>{
            var p=new Coffee.UIExtensions.UIParticle();var a=new UnityEngine.ParticleSystem();var b=new UnityEngine.ParticleSystem();p.particles.Add(a);p.particles.Add(b);
            var setting=new ParticleUpdateSetting(p);var one=new HashSet<UnityEngine.ParticleSystem>{b};
            setting.Apply(new HashSet<UnityEngine.ParticleSystem>(),false);setting.Apply(one,false);
            Assert(p.enabled && p.plays==1 && p.particles.Count==1 && ReferenceEquals(p.particles[0],b));
        });
        Test("restore preserves disabled components",()=>{
            var p=new Coffee.UIExtensions.UIParticle {enabled=false};var ps=new UnityEngine.ParticleSystem();p.particles.Add(ps);var setting=new ParticleUpdateSetting(p);
            setting.Apply(new HashSet<UnityEngine.ParticleSystem>(),false);setting.Apply(new HashSet<UnityEngine.ParticleSystem>{ps},false);Assert(!p.enabled && p.plays==0);
        });
        Test("restore honors pause",()=>{
            var p=new Coffee.UIExtensions.UIParticle();var ps=new UnityEngine.ParticleSystem();p.particles.Add(ps);var setting=new ParticleUpdateSetting(p);
            setting.Apply(new HashSet<UnityEngine.ParticleSystem>(),true);setting.Apply(new HashSet<UnityEngine.ParticleSystem>{ps},true);
            Assert(p.enabled && p.plays==1 && p.pauses==1);
        });
        Test("selection never scales system parameters",()=>{
            var ps=new UnityEngine.ParticleSystem();var setting=new ParticleQuantitySetting(ps);
            setting.Apply(false);Assert(!ps.emission.enabled && ps.main.maxParticles==200 && ps.emission.rateOverTimeMultiplier==40 && ps.emission.bursts[0].count.constant==40);
            setting.Apply(true);Assert(ps.emission.enabled && ps.main.maxParticles==200 && ps.emission.rateOverTimeMultiplier==40 && ps.emission.rateOverDistanceMultiplier==8 && ps.emission.bursts[0].count.constant==40);
        });
        Test("original disabled emission stays disabled",()=>{
            var ps=new UnityEngine.ParticleSystem();ps.emission.enabled=false;var setting=new ParticleQuantitySetting(ps);setting.Apply(false);setting.Apply(true);Assert(!ps.emission.enabled);
        });
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");return failed==0?0:1;
    }
}
