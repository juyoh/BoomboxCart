using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using BepInEx.Logging;
using BoomBoxCartMod.Patches;
using System;

namespace BoomBoxCartMod
{
	public class BoomboxController : MonoBehaviourPun
	{
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		private PhysGrabCart cart;
		private BoomboxUI boomboxUI;
		private Boombox boombox;

		private int currentControllerId = -1;

		private void Awake()
		{
			cart = GetComponent<PhysGrabCart>();
			boombox = GetComponent<Boombox>();

			if (cart == null)
			{
				Logger.LogError("BoomboxController: PhysGrabCart component not found!");
				return;
			}

			if (boombox == null)
			{
				Logger.LogError("BoomboxController: Boombox component not found!");
				return;
			}
		}

		private bool IsLocalPlayerGrabbingCart()
		{
			return PlayerGrabbingTracker.IsLocalPlayerGrabbingCart(gameObject);
		}

		public void RequestBoomboxControl()
		{
			if (!IsLocalPlayerGrabbingCart())
			{
				//Logger.LogInfo("Cannot request boombox control - local player is not grabbing the cart");
				return;
			}

			int localPlayerId = PhotonNetwork.LocalPlayer.ActorNumber;
			//Logger.LogInfo($"Local player {localPlayerId} requesting boombox control");

			photonView.RPC("RequestControl", RpcTarget.MasterClient, localPlayerId);
			if (!PhotonNetwork.IsConnected)
			{
				// singleplayer
				SetController(localPlayerId);
			}
		}

		[PunRPC]
		private void RequestControl(int requesterId)
		{
			if (!PhotonNetwork.IsMasterClient)
				return;

			//Logger.LogInfo($"Master client processing control request from player {requesterId}");

			// check if requester is actually grabbing the cart
			bool validRequest = true;
			if (requesterId == PhotonNetwork.LocalPlayer.ActorNumber)
			{
				validRequest = PlayerGrabbingTracker.IsLocalPlayerGrabbingCart(gameObject);
			}

			// can only give control if nobody else grabbing that thang
			if (currentControllerId == -1 && validRequest)
			{
				// Grant control to requester
				//Logger.LogInfo($"Granting control to player {requesterId}");
				photonView.RPC("SetController", RpcTarget.All, requesterId);
			}
			else
			{
				//Logger.LogInfo($"Control request denied. Current controller: {currentControllerId}, Valid request: {validRequest}");
			}
		}

		[PunRPC]
		private void SetController(int controllerId)
		{
			try
			{
				//Logger.LogInfo($"Boombox control given to player {controllerId}");
				currentControllerId = controllerId;

				if (controllerId == PhotonNetwork.LocalPlayer.ActorNumber)
				{
					EnsureBoomboxUIExists();
					if (boomboxUI != null)
					{
						boomboxUI.ShowUI();
					}
					else
					{
						Logger.LogError("Failed to create BoomboxUI component");
					}
				}
				else if (boomboxUI != null && boomboxUI.IsUIVisible())
				{
					boomboxUI.HideUI();
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error in SetController: {ex.Message}\n{ex.StackTrace}");
			}
		}

		private void EnsureBoomboxUIExists()
		{
			if (boomboxUI == null)
			{
				boomboxUI = gameObject.GetComponent<BoomboxUI>();
				if (boomboxUI == null)
				{
					boomboxUI = gameObject.AddComponent<BoomboxUI>();
					//Logger.LogInfo("BoomboxUI component added to gameObject");
				}
			}
		}

		public void ReleaseControl()
		{
			if (currentControllerId == PhotonNetwork.LocalPlayer.ActorNumber)
			{
				if (PhotonNetwork.IsConnected)
				{
					photonView.RPC("RequestRelease", RpcTarget.MasterClient, PhotonNetwork.LocalPlayer.ActorNumber);
				}
				else
				{
					//singleplayer
					RequestRelease(PhotonNetwork.LocalPlayer.ActorNumber);
				}
			}
		}

		[PunRPC]
		private void RequestRelease(int releaserId)
		{
			if (!PhotonNetwork.IsMasterClient)
				return;

			if (currentControllerId == releaserId)
			{
				if (PhotonNetwork.IsConnected)
				{
					//Logger.LogInfo($"Master client processing release request from player {releaserId}");
					photonView.RPC("SetController", RpcTarget.All, -1);
				}
				else
				{
					//singleplayer
					SetController(-1);
				}
			}
		}

		private void OnPlayerReleasedCart(int playerActorNumber)
		{
			// if the player releasing the cart is the one controlling the boombox, automatically release control
			if (playerActorNumber == currentControllerId && PhotonNetwork.IsMasterClient)
			{
				//Logger.LogInfo($"Player {playerActorNumber} released cart while controlling boombox - auto-releasing control");
				photonView.RPC("SetController", RpcTarget.All, -1);
			}
		}

		public void LocalPlayerReleasedCart()
		{
			int localPlayerId = PhotonNetwork.LocalPlayer.ActorNumber;

			if (currentControllerId == localPlayerId)
			{
				//Logger.LogInfo($"Local player {localPlayerId} released cart while controlling boombox");
				ReleaseControl();
			}
		}
	}
}
