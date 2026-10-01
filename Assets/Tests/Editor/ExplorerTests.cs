using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using Object=UnityEngine.Object;

namespace Voyage.Tests.Editor
{
    public sealed class ExplorerTests
    {
        GameObject root,player,a,b,wall;
        Component explorer;
        Type TypeOf(string name)=>Type.GetType("Voyage.Exploration."+name+", Assembly-CSharp",true);
        object Call(string method,params object[] args)
        {
            var candidates = explorer.GetType().GetMethods();
            foreach (var candidate in candidates)
            {
                if (candidate.Name != method) continue;
                var parameters = candidate.GetParameters();
                if (parameters.Length == args.Length) return candidate.Invoke(explorer,args);
                if (parameters.Length == args.Length + 1 && parameters[args.Length].IsOptional)
                {
                    var expanded = new object[parameters.Length];
                    args.CopyTo(expanded,0);
                    expanded[args.Length] = parameters[args.Length].DefaultValue;
                    return candidate.Invoke(explorer,expanded);
                }
            }
            throw new MissingMethodException(method);
        }
        object Property(string name)=>explorer.GetType().GetProperty(name).GetValue(explorer);
        [SetUp] public void Setup()
        {
            root=new GameObject("Explorer test");root.transform.position=new Vector3(10000,500,10000);
            player=Object.Instantiate(Resources.Load<GameObject>("Prefabs/ExplorerPlayer"),root.transform);
            player.transform.localPosition=Vector3.zero;explorer=player.GetComponent(TypeOf("ExplorerPlayer"));
        }
        [TearDown] public void Cleanup(){if(root!=null)Object.DestroyImmediate(root);}
        GameObject Npc(Vector3 offset)
        {
            var npc=Object.Instantiate(Resources.Load<GameObject>("Prefabs/NpcCapsule"),root.transform);
            npc.transform.localPosition=offset;
            // EditMode instantiation does not run gameplay OnEnable.
            var actor=npc.GetComponent(TypeOf("NpcActor"));
            actor.GetType().GetMethod("OnEnable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(actor,null);
            return npc;
        }
        [Test] public void PrefabsHaveCapsulesAndSeparateRoles()
        {
            Assert.That(player.GetComponent<CharacterController>(),Is.Not.Null);
            Assert.That(player.GetComponentInChildren<MeshFilter>().sharedMesh.name,Does.Contain("Capsule"));
            a=Npc(Vector3.right*4);Assert.That(a.GetComponent<CapsuleCollider>(),Is.Not.Null);
            Assert.That(a.GetComponent(TypeOf("NpcActor")),Is.Not.Null);
        }

        [Test] public void SprintMoveUsesHigherSpeed()
        {
            var move = explorer.GetType().GetMethod("Move", new[] { typeof(Vector2), typeof(bool), typeof(float), typeof(bool) });
            Assert.That(move, Is.Not.Null);
            move.Invoke(explorer, new object[] { Vector2.up, false, .1f, false });
            float walkingDistance = ((Component)explorer).transform.localPosition.magnitude;
            ((Component)explorer).transform.localPosition = Vector3.zero;
            move.Invoke(explorer, new object[] { Vector2.up, false, .1f, true });
            float sprintDistance = ((Component)explorer).transform.localPosition.magnitude;
            Assert.That(sprintDistance, Is.GreaterThan(walkingDistance * 1.4f));
        }
        [Test] public void DistanceSelectsTargetAndTransitionsQuestionToExclamation()
        {
            a=Npc(Vector3.forward*4);b=Npc(Vector3.back*5);Physics.SyncTransforms();Call("RefreshTarget");
            Assert.That(Property("Target"),Is.EqualTo(a.GetComponent(TypeOf("NpcActor"))));
            Assert.That(Property("Hint"),Is.EqualTo("?"));
            b.transform.localPosition=Vector3.back*2;Physics.SyncTransforms();Call("RefreshTarget");
            Assert.That(Property("Target"),Is.EqualTo(b.GetComponent(TypeOf("NpcActor"))),"A target behind the player is still selected by distance.");
            Assert.That(Property("Hint"),Is.EqualTo("!"));
            int calls=0;((UnityEvent<GameObject>)TypeOf("NpcActor").GetField("onInteract").GetValue(b.GetComponent(TypeOf("NpcActor")))).AddListener(_=>calls++);
            Assert.That(Call("TryInteract"),Is.EqualTo(true));Assert.That(calls,Is.EqualTo(1));
        }
        [Test] public void WallsAndDisabledObjectsCannotBeInteractedWith()
        {
            a=Npc(Vector3.forward*2);
            wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.SetParent(root.transform,false);
            wall.transform.localPosition=new Vector3(0,1,1);wall.transform.localScale=new Vector3(2,3,.25f);
            Physics.SyncTransforms();Assert.That(Call("TryInteract"),Is.EqualTo(false));
            Object.DestroyImmediate(wall);a.SetActive(false);Physics.SyncTransforms();Call("RefreshTarget");
            Assert.That(Property("Target"),Is.Null);Assert.That(Property("Hint"),Is.EqualTo(""));
        }
        [Test] public void ControllerWalksAndJumpsWithoutDiagonalSpeedBoost()
        {
            var ground=GameObject.CreatePrimitive(PrimitiveType.Cube);ground.transform.SetParent(root.transform,false);
            ground.transform.localPosition=new Vector3(0,-.5f,0);ground.transform.localScale=new Vector3(100,1,100);
            Physics.SyncTransforms();
            for(int i=0;i<10;i++)Call("Move",Vector2.zero,false,.02f);
            float y=player.transform.position.y;
            Call("Move",Vector2.zero,true,.02f);
            Assert.That(player.transform.position.y,Is.GreaterThan(y+.03f));
            Vector3 start=player.transform.position;Call("Move",Vector2.one,false,.1f);
            Assert.That(Vector3.ProjectOnPlane(player.transform.position-start,Vector3.up).magnitude,Is.EqualTo(.45f).Within(.015f));
        }
    }
}
