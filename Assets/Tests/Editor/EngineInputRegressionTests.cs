using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;
namespace Voyage.Tests.Editor
{
    public class EngineInputRegressionTests
    {
        const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static Type T(string name)=>Type.GetType(name+", Assembly-CSharp",true);
        static object Call(object obj,string name,params object[] args)=>obj.GetType().GetMethod(name,Flags).Invoke(obj,args);
        static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,Flags).SetValue(obj,value);
        static object Get(object obj,string name)=>obj.GetType().GetField(name,Flags).GetValue(obj);
        GameObject go; Component car; WheelCollider wheel; Rigidbody body; object previousFuel;
        PropertyInfo Fuel=>T("FuelTank").GetProperty("SharedFuel");
        [SetUp] public void Setup()
        {
            previousFuel=Fuel.GetValue(null); Fuel.SetValue(null,100f);
            go=new GameObject("Engine input test"); go.SetActive(false); body=go.AddComponent<Rigidbody>();
            car=go.AddComponent(T("CarControl"));
            var child=new GameObject("Wheel"); child.transform.SetParent(go.transform);
            wheel=child.AddComponent<WheelCollider>(); var control=child.AddComponent(T("WheelControl"));
            Call(control,"BindCollider",wheel); Set(control,"motorized",true);
            var wheels=Array.CreateInstance(control.GetType(),1); wheels.SetValue(control,0);
            Set(car,"wheels",wheels); Set(car,"rigidBody",body); Set(car,"engineOn",true); Set(car,"electricalPowerOn",true);
            Set(car,"currentGear",Enum.Parse(T("CarControl").GetNestedType("GearMode"),"Drive"));
            go.SetActive(true); // PhysX ignores velocity/torque writes on inactive bodies.
        }
        [TearDown] public void Cleanup(){Object.DestroyImmediate(go); Fuel.SetValue(null,previousFuel);}

        [Test] public void BrakeWinsWhenWAndSAreHeldTogether()
        {
            float pedal=(float)Call(car,"ResolvePedalInput",1f,true,true);
            Call(car,"UpdateDriving",pedal,0f,true,false);
            Assert.That(wheel.motorTorque,Is.Zero); Assert.That(wheel.brakeTorque,Is.GreaterThan(0));
            Assert.That(Get(car,"engineSoundRequested"),Is.False);
        }
        [TestCase("Drive",-1)] [TestCase("Reverse",1)] [TestCase("Neutral",0)] [TestCase("Park",0)]
        public void WUsesGearDirectionButKeepsEngineDemandInNeutral(string gear,int direction)
        {
            Set(car,"currentGear",Enum.Parse(T("CarControl").GetNestedType("GearMode"),gear));
            Call(car,"UpdateDriving",1f,0f,true,false);
            Assert.That(Math.Sign(wheel.motorTorque),Is.EqualTo(direction));
            Assert.That(Get(car,"engineSoundRequested"),Is.True);
            if(direction==0) Assert.That((float)Get(car,"smoothEngineRpm"),Is.GreaterThan(500f));
        }
        [Test] public void HandbrakeSuppressesDriveAndWReleaseClearsTorque()
        {
            Call(car,"UpdateDriving",1f,0f,true,true);
            Assert.That(wheel.motorTorque,Is.Zero); Assert.That(wheel.brakeTorque,Is.GreaterThan(0));
            Assert.That(Get(car,"engineSoundRequested"),Is.True);
            Call(car,"UpdateDriving",1f,0f,true,false); Assert.That(wheel.motorTorque,Is.LessThan(0));
            Call(car,"UpdateDriving",0f,0f,true,false); Assert.That(wheel.motorTorque,Is.Zero);
            Assert.That(Get(car,"engineSoundRequested"),Is.False);
        }
        [TestCase("engineOn")] [TestCase("electricalPowerOn")] [TestCase("fuel")]
        public void UnavailableEngineCannotDriveOrRequestAudio(string disabled)
        {
            if(disabled=="fuel") Fuel.SetValue(null,0f); else Set(car,disabled,false);
            body.linearVelocity=Vector3.back*50f;
            Call(car,"UpdateDriving",1f,0f,true,false);
            Assert.That(wheel.motorTorque,Is.Zero); Assert.That(Get(car,"engineSoundRequested"),Is.False);
            Assert.That(body.linearVelocity.z,Is.EqualTo(-50f),"Engine state must not truncate coasting momentum.");
        }
        [Test] public void LosingPowerOrControlClearsPreviouslyHeldW()
        {
            Call(car,"UpdateDriving",1f,0f,true,false);
            Call(car,"SetElectricalPower",false);
            Assert.That(wheel.motorTorque,Is.Zero); Assert.That(Get(car,"engineSoundRequested"),Is.False);
            Call(car,"SetElectricalPower",true); Call(car,"UpdateDriving",1f,0f,false,false);
            Assert.That(wheel.motorTorque,Is.Zero); Assert.That(Get(car,"engineSoundRequested"),Is.False);
        }
        [Test] public void ShiftCutsTorqueButDoesNotInterruptHeldWAudio()
        {
            Set(car,"shiftTimer",.5f);
            Call(car,"UpdateDriving",1f,0f,true,false);
            Assert.That(wheel.motorTorque,Is.Zero); Assert.That(Get(car,"engineSoundRequested"),Is.True);
            var samples=new float[1024]; Call(car,"OnAudioFilterRead",samples,2);
            Assert.That(Array.Exists(samples,x=>Mathf.Abs(x)>.00001f),Is.True);
        }
        [Test] public void FocusLossImmediatelyClearsDriveAndSuspendsSound()
        {
            Call(car,"UpdateDriving",1f,0f,true,false);
            Call(car,"OnApplicationFocus",false);
            Assert.That(wheel.motorTorque,Is.Zero); Assert.That(Get(car,"engineSoundRequested"),Is.False);
            var samples=new float[1024]; Call(car,"OnAudioFilterRead",samples,2);
            Assert.That(samples,Is.All.EqualTo(0f));
            Call(car,"OnApplicationFocus",true);
            Assert.That(Get(car,"engineAudioSuspended"),Is.False);
        }
        [Test] public void ShortTapRetainsFullTailAndSuspensionFreezesAudio()
        {
            Set(car,"samplingRate",48000d); Set(car,"engineSoundRequested",true);
            Call(car,"OnAudioFilterRead",new float[240],1); // 5 ms tap, before attack finishes.
            Set(car,"engineSoundRequested",false);
            var block=new float[480];
            for(int i=0;i<80;i++) Call(car,"OnAudioFilterRead",block,1);
            Assert.That(Array.Exists(block,x=>Mathf.Abs(x)>.000001f),Is.True,"A short tap must not truncate the release to a few milliseconds.");
            object gain=Get(car,"engineAudioEnvelope");
            Set(car,"engineAudioSuspended",true); Call(car,"OnAudioFilterRead",block,1);
            Assert.That(block,Is.All.EqualTo(0f)); Assert.That(Get(car,"engineAudioEnvelope"),Is.EqualTo(gain));
            Set(car,"engineAudioSuspended",false);
            for(int i=0;i<60;i++) Call(car,"OnAudioFilterRead",block,1);
            Assert.That(block,Is.All.EqualTo(0f));
        }
        [Test] public void VisualInitializationCannotReplaceReferenceEngineAudio()
        {
            go.SetActive(false);
            var source=go.GetComponent<AudioSource>();
            var clip=AudioClip.Create("DSP ownership test",480,1,48000,false);
            try
            {
                source.clip=clip; source.pitch=1f; source.volume=1f;
                var player=go.AddComponent(T("PlayerCar"));
                Call(player,"InitializeEngineAudio");
                Assert.That(source.clip,Is.SameAs(clip));
                Assert.That(source.pitch,Is.EqualTo(1f)); Assert.That(source.volume,Is.EqualTo(1f));
            }
            finally { source.clip=null; Object.DestroyImmediate(clip); }
        }
    }
}
