﻿using System;
using System.Collections.Generic;
using Assets.Scripts.MapEditor;
using Assets.Scripts.Sprites;
using Assets.Scripts.Utility;
using Lidgren.Network;
using RebuildData.Shared.Data;
using RebuildData.Shared.Enum;
using RebuildData.Shared.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Network
{
	class NetworkManager : MonoBehaviour
	{
		public static NetworkManager Instance;

		public CameraFollower CameraFollower;
		public Dictionary<int, ServerControllable> entityList = new Dictionary<int, ServerControllable>();
		public int PlayerId;

		private RoWalkDataProvider walkProvider;
		private static NetClient client;
		private NetIncomingMessage inc;
		
		private float lastPing = 0;
		private bool isConnected = false;
		private bool isLoggedIn = false;

		private string currentMap = "";

		private List<Vector2Int> pathData = new List<Vector2Int>(20);

		private Scene currentScene;

#if DEBUG
		public static string SpawnMap = "";
#endif

		private void Start()
		{
			Debug.Log("Starting Network Manager");

			Instance = this;

			NetPeerConfiguration config = new NetPeerConfiguration("RebuildZoneServer");

#if DEBUG
			UnloadOldScenes();

			config.SimulatedMinimumLatency = 0.1f;
			//config.SimulatedLoss = 0.1f;
#endif


			client = new NetClient(config);
			
			NetOutgoingMessage outMsg = client.CreateMessage();


			client.Start();

			outMsg.Write("A Client");

			client.Connect("70.65.94.184", 14248, outMsg);

			Debug.Log("Waiting for connection to server...");

			//SendPing();

			lastPing = Time.time;
		}

		private void UnloadOldScenes()
		{
			var sceneCount = SceneManager.sceneCount;
			for (var i = 0; i < sceneCount; i++)
			{
				var scene = SceneManager.GetSceneAt(i);
				if (scene.name != "MainScene")
					SceneManager.UnloadSceneAsync(scene);
			}
			
			CameraFollower.Target = null;

			entityList.Clear();
		}

		private Vector2Int ReadPosition(NetIncomingMessage msg)
		{
			var x = msg.ReadInt16();
			var y = msg.ReadInt16();
			return new Vector2Int(x, y);
		}

		private void LoadMoveData(NetIncomingMessage msg, ServerControllable ctrl)
		{
			var moveSpeed = msg.ReadFloat();
			var moveCooldown = msg.ReadFloat();
			var totalSteps = (int)msg.ReadByte();
			var curStep = (int) msg.ReadByte();

			pathData.Clear();
			for (var i = 0; i < totalSteps; i++)
				pathData.Add(ReadPosition(msg));

			//if(ctrl.Id == PlayerId)
			//	Debug.Log("Doing move for player!");

			ctrl.StartMove(moveSpeed, moveCooldown, totalSteps, curStep, pathData);
		}

		private ServerControllable SpawnEntity(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();
			var type = (CharacterType)msg.ReadByte();
			var classId = msg.ReadInt16();
			var pos = ReadPosition(msg);
			var facing = (FacingDirection)msg.ReadByte();
			var state = (CharacterState)msg.ReadByte();
			
			ServerControllable controllable;
			if (type == CharacterType.Player)
			{
				var headFacing = (HeadFacing)msg.ReadByte();
				var headId = msg.ReadByte();
				var isMale = msg.ReadBoolean();

				var playerData = new PlayerSpawnParameters()
				{
					ServerId = id,
					ClassId = classId,
					Facing = facing,
					Position = pos,
					State = state,
					HeadFacing = headFacing,
					HeadId = headId,
					IsMale = isMale
				};

				controllable = SpriteDataLoader.Instance.InstantiatePlayer(ref playerData);
			}
			else
			{
				var monData = new MonsterSpawnParameters()
				{
					ServerId = id,
					ClassId = classId,
					Facing = facing,
					Position = pos,
					State = state
				};
				controllable = SpriteDataLoader.Instance.InstantiateMonster(ref monData);
			}
			
			entityList.Add(id, controllable);

			if (controllable.SpriteMode == ClientSpriteType.Prefab)
				return controllable;

			if (state == CharacterState.Moving)
				LoadMoveData(msg, controllable);
			if (state == CharacterState.Sitting)
			{
				controllable.SpriteAnimator.ChangeMotion(SpriteMotion.Sit);
				controllable.SpriteAnimator.State = SpriteState.Sit;
			}

			if (PlayerId == controllable.Id)
			{
				CameraFollower.Target = controllable.gameObject;
				//Debug.Log($"Player entity sent, we're at position {pos}");

				SceneTransitioner.Instance.FadeIn();
				CameraFollower.Instance.SnapLookAt();
			}

			return controllable;
		}


		private void OnMessageChangeSitStand(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();
			var isSitting = msg.ReadBoolean();

			if (!entityList.TryGetValue(id, out var controllable))
			{
				Debug.LogWarning("Trying to move entity " + id + ", but it does not exist in scene!");
				return;
			}

			if (isSitting)
			{
				controllable.SpriteAnimator.ChangeMotion(SpriteMotion.Sit);
				controllable.SpriteAnimator.State = SpriteState.Sit;
				return;
			}

			if (controllable.SpriteAnimator.State == SpriteState.Sit)
			{
				controllable.SpriteAnimator.ChangeMotion(SpriteMotion.Idle);
				controllable.SpriteAnimator.State = SpriteState.Idle;
			}
		}

		private void OnMessageChangeFacing(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();
			var facing = (FacingDirection)msg.ReadByte();

			if (!entityList.TryGetValue(id, out var controllable))
			{
				Debug.LogWarning("Trying to move entity " + id + ", but it does not exist in scene!");
				return;
			}

			controllable.SpriteAnimator.Direction = facing;
			if (controllable.SpriteAnimator.Type == SpriteType.Player)
				controllable.SpriteAnimator.SetHeadFacing((HeadFacing)msg.ReadByte());
		}

		private void OnMessageCreateEntity(NetIncomingMessage msg)
		{
			SpawnEntity(msg);
		}

		private void OnMessageMove(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();

			if (!entityList.TryGetValue(id, out var controllable))
			{
				Debug.LogWarning("Trying to move entity " + id + ", but it does not exist in scene!");
				return;
			}

			controllable.MovePosition(ReadPosition(msg));
		}

		private void OnMessageStartMove(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();

			if (!entityList.TryGetValue(id, out var controllable))
			{
				Debug.LogWarning("Trying to move entity " + id + ", but it does not exist in scene!");
				return;
			}
			
			LoadMoveData(msg, controllable);
		}

		private void OnMessageChangeMaps(NetIncomingMessage msg)
		{
			var mapName = msg.ReadString();

			entityList.Clear();
			
			currentMap = mapName;
			//var mapLoad = SceneManager.LoadSceneAsync(mapName, LoadSceneMode.Additive);
			//mapLoad.completed += OnMapLoad;

			SceneTransitioner.Instance.DoTransitionToScene(currentScene, currentMap, OnMapLoad);
			
			//SceneManager.UnloadSceneAsync(currentScene);
		}

		private void OnMessageEnterServer(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();
			var mapName = msg.ReadString();

			Debug.Log($"We're id {id} on map {mapName}");

			currentMap = mapName;

			//var mapLoad = SceneManager.LoadSceneAsync(mapName, LoadSceneMode.Additive);
			//mapLoad.completed += OnMapLoad;

			PlayerId = id;

			SceneTransitioner.Instance.LoadScene(currentMap, OnMapLoad);
		}

		private void OnMessageRemoveEntity(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();

			if (!entityList.TryGetValue(id, out var controllable))
			{
				Debug.LogWarning("Trying to remove entity " + id + ", but it does not exist in scene!");
				return;
			}

			entityList.Remove(id);

			if (id == PlayerId)
			{
				Debug.LogWarning("Whoa! Trying to delete player object. Is that right...?");
				CameraFollower.Instance.Target = null;
			}

			GameObject.Destroy(controllable.gameObject);
		}

		private void OnMessageRemoveAllEntities(NetIncomingMessage msg)
		{
			foreach (var entity in entityList)
			{
				GameObject.Destroy(entity.Value.gameObject);
			}

			entityList.Clear();
		}

		private void OnMessageStopPlayer(NetIncomingMessage msg)
		{
			var id = msg.ReadInt32();

			if (!entityList.TryGetValue(id, out var controllable))
			{
				Debug.LogWarning("Trying to stop entity " + id + ", but it does not exist in scene!");
				return;
			}

			controllable.StopWalking();
		}

		void HandleDataPacket(NetIncomingMessage msg)
		{
			var type = (PacketType)msg.ReadByte();

			//Debug.Log(type);

			switch (type)
			{
				case PacketType.StartMove:
					OnMessageStartMove(msg);
					break;
				case PacketType.RemoveAllEntities:
					OnMessageRemoveAllEntities(msg);
					break;
				case PacketType.RemoveEntity:
					OnMessageRemoveEntity(msg);
					break;
				case PacketType.CreateEntity:
					OnMessageCreateEntity(msg);
					break;
				case PacketType.EnterServer:
					OnMessageEnterServer(msg);
					break;
				case PacketType.LookTowards:
					OnMessageChangeFacing(msg);
					break;
				case PacketType.SitStand:
					OnMessageChangeSitStand(msg);
					break;
				case PacketType.ChangeMaps:
					OnMessageChangeMaps(msg);
					break;
				case PacketType.StopAction:
					OnMessageStopPlayer(msg);
					break;
				case PacketType.Move:
					OnMessageMove(msg);
					break;
				default:
					Debug.LogWarning($"Failed to handle packet type: {type}");
					break;
			}
		}

		private void DoPacketHandling()
		{
			while (client.ReadMessage(out var msg))
			{
				switch (msg.MessageType)
				{
					case NetIncomingMessageType.Data:
						HandleDataPacket(msg);
						break;
					case NetIncomingMessageType.DebugMessage:
					case NetIncomingMessageType.VerboseDebugMessage:
					case NetIncomingMessageType.WarningMessage:
					case NetIncomingMessageType.ErrorMessage:
						Debug.Log(msg.MessageType + ": " + msg.ReadString());
						break;
					case NetIncomingMessageType.StatusChanged:
						Debug.Log("Status changed: " + client.Status);
						break;
					default:
						Debug.Log("We received a packet type we didn't handle: " + msg.MessageType);
						break;
				}
			}
		}
		
		private void OnMapLoad()
		{
			var msg = client.CreateMessage();

			msg.Write((byte)PacketType.PlayerReady);

			currentScene = SceneManager.GetSceneByName(currentMap);
			SceneManager.SetActiveScene(currentScene);
			
			client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
		}
		
		public void ChangePlayerSitStand(bool isChangingToSitting)
		{
			var msg = client.CreateMessage();

			msg.Write((byte)PacketType.SitStand);
			msg.Write(isChangingToSitting);

			client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
		}

		public void ChangePlayerFacing(FacingDirection direction, HeadFacing headFacing)
		{
			var msg = client.CreateMessage();

			msg.Write((byte)PacketType.LookTowards);
			msg.Write((byte)direction);
			msg.Write((byte)headFacing);

			client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
		}

		public void RandomTeleport()
		{
			var msg = client.CreateMessage();

			msg.Write((byte)PacketType.RandomTeleport);

			client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
		}

		public void MovePlayer(Vector2Int position)
		{
			var msg = client.CreateMessage();
			//Debug.Log(position);
			msg.Write((byte)PacketType.StartMove);
			msg.Write((short)position.x);
			msg.Write((short)position.y);

			client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
		}

		public void StopPlayer()
		{
			var msg = client.CreateMessage();

			msg.Write((byte)PacketType.StopAction);

			client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
		}

		private void SendPing()
		{
			var msg = client.CreateMessage();
			msg.Write((byte)PacketType.Ping);

			client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
		}

		private void Update()
		{
			if (client.Status == NetPeerStatus.Running)
				DoPacketHandling();
			
			if (client.ConnectionStatus == NetConnectionStatus.Connected && !isConnected)
			{
				SendPing();
				var msg = client.CreateMessage();
#if DEBUG
				if (!string.IsNullOrWhiteSpace(SpawnMap))
				{
					msg.Write((byte)PacketType.EnterServerSpecificMap);
					msg.Write(SpawnMap);

					var prefx = PlayerPrefs.GetInt("DebugStartX", -1);
					var prefy = PlayerPrefs.GetInt("DebugStartY", -1);

					Debug.Log(prefx + " : " + prefy);

					if (prefx > 0 && prefy > 0)
					{
						msg.Write(true);
						msg.Write((short)prefx);
						msg.Write((short)prefy);
						PlayerPrefs.DeleteKey("DebugStartX");
						PlayerPrefs.DeleteKey("DebugStartY");

					}
					msg.Write(false);
				}
				else
					msg.Write((byte)PacketType.EnterServer);
#else
				msg.Write((byte)PacketType.EnterServer);
#endif
				client.SendMessage(msg, NetDeliveryMethod.ReliableOrdered);
				isConnected = true;
			}

			if (isConnected && client.ServerConnection == null)
			{
				isConnected = false;
				Debug.LogWarning("Client is now disconnected!");
			}

			if (!isConnected)
				return;


			if (client.ConnectionStatus == NetConnectionStatus.Disconnected)
			{
				Console.WriteLine("Disconnected!");
				return;
			}

			if (lastPing + 5 < Time.time)
			{
				SendPing();
				//Debug.Log("Sending keep alive packet.");

				lastPing = Time.time;
			}
		}
		
		public void OnApplicationQuit()
		{
			NetOutgoingMessage outmsg = client.CreateMessage();
			outmsg.Write((byte)PacketType.Disconnect);
			client.SendMessage(outmsg, NetDeliveryMethod.ReliableOrdered);
		}
	}
}