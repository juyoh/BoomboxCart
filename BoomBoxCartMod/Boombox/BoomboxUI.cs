using UnityEngine;
using System.Text.RegularExpressions;
using Photon.Pun;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using BepInEx.Logging;
using BoomboxCartMod;
using System;
using Photon.Chat;

namespace BoomboxCartMod
{
	public class BoomboxUI : MonoBehaviourPun
	{
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		public PhotonView photonView;
		private bool showUI = false;
		private string urlInput = "";

		private float normalizedVolume = 0.3f; // norm volume is 0-1, actual volume is 0-maxVolumeLimit (in boombox.cs) ((1 is so loud))
		private float lastSentNormalizedVolume = 0.3f;
		private bool isSliderBeingDragged = false; // used to apply changes on slider release

		private int qualityLevel = 3;
		private string[] qualityLabels = new string[] { "REALLY Low (You Freak)", "Low", "Medium-Low", "Medium-High (Recommended)", "High" };
		private bool isQualitySliderBeingDragged = false;
		private int lastSentQualityLevel = 3;

		private Rect windowRect;
		private Boombox boombox;
		private BoomboxController controller;

		private GUIStyle windowStyle;
		private GUIStyle headerStyle;
		private GUIStyle buttonStyle;
		private GUIStyle smallButtonStyle;
		private GUIStyle textFieldStyle;
		private GUIStyle labelStyle;
		private GUIStyle sliderStyle;
		private GUIStyle statusStyle;
		private GUIStyle scrollViewStyle;
		private Texture2D backgroundTexture;
		private Texture2D buttonTexture;
		private Texture2D sliderBackgroundTexture;
		private Texture2D sliderThumbTexture;
		private Texture2D textFieldBackgroundTexture;

		private Vector2 urlScrollPosition = Vector2.zero;
		//private string urlInputDisplay = "";
		private float textFieldVisibleWidth = 350;

		private string errorMessage = "";
		private float errorMessageTime = 0f;
		private string statusMessage = "";

		private CursorLockMode previousLockMode;
		private bool previousCursorVisible;
		private bool stylesInitialized = false;
		private Vector2 scrollPosition = Vector2.zero;
		private Vector2 historyScrollPosition = Vector2.zero;

		private bool shouldClearFocus = false;

		private void Awake()
		{
			try
			{
				boombox = GetComponent<Boombox>();
				if (boombox != null)
				{
					photonView = boombox.photonView;
				}
				else
				{
					Logger.LogError("BoomboxUI: Failed to find Boombox component");
					photonView = GetComponent<PhotonView>();
				}

				controller = GetComponent<BoomboxController>();

				if (photonView == null)
				{
					Logger.LogError("BoomboxUI: Failed to find PhotonView component");
				}

				windowRect = new Rect(Screen.width / 2 - 200, Screen.height / 2 - 175, 400, 590 + Boombox.historyEntries.Length * 35);

				//Logger.LogInfo($"BoomboxUI initialized. Boombox: {boombox}, PhotonView: {photonView}, Controller: {controller}");
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error in BoomboxUI.Awake: {ex.Message}\n{ex.StackTrace}");
			}
		}

		private void Update()
		{
			if (Time.time > errorMessageTime && !string.IsNullOrEmpty(errorMessage))
			{
				errorMessage = "";
			}

			if (showUI && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
			{
				if (controller != null)
				{
					//Logger.LogInfo($"Player {PhotonNetwork.LocalPlayer.ActorNumber} releasing boombox control");
					controller.ReleaseControl();
				}
				else
				{
					HideUI();
				}
			}

			if (isSliderBeingDragged && Input.GetMouseButtonUp(0))
			{
				isSliderBeingDragged = false;
				//Logger.LogInfo("Volume slider released, sending volume update");
				SendVolumeUpdate();
			}

			if (isQualitySliderBeingDragged && Input.GetMouseButtonUp(0))
			{
				isQualitySliderBeingDragged = false;
				//Logger.LogInfo("Quality slider released, sending qualtiy update");
				SendQualityUpdate();
			}
		}

		public void ShowUI()
		{
			if (!showUI)
			{
				if (boombox == null || photonView == null)
				{
					Logger.LogError("Cannot show UI - boombox or photonView is null");
					return;
				}

				showUI = true;

				previousLockMode = Cursor.lockState;
				previousCursorVisible = Cursor.visible;

				Cursor.visible = true;
				Cursor.lockState = CursorLockMode.None;

				if (boombox != null)
				{
					normalizedVolume = boombox.audioSource.volume / boombox.maxVolumeLimit;
					lastSentNormalizedVolume = normalizedVolume;
					qualityLevel = boombox.qualityLevel;
					lastSentQualityLevel = qualityLevel;
				}

				//Logger.LogInfo("BoomboxUI shown");

				UpdateStatusFromBoombox();
			}
		}

		public void UpdateStatusFromBoombox()
		{
			if (boombox != null)
			{
				if (boombox.IsDownloadInProgress())
				{
					statusMessage = $"Downloading audio from {boombox.GetCurrentDownloadUrl()}...";
				}
				else if (boombox.isPlaying)
				{
					statusMessage = $"Now playing: {boombox.currentSongTitle}";
				}
				else if (!string.IsNullOrEmpty(boombox.currentSongUrl))
				{
					statusMessage = $"Ready to play: {boombox.currentSongTitle}";
				}
				else
				{
					statusMessage = "Ready to play music! Enter a Video URL";
				}
			}
		}

		private void SendVolumeUpdate()
		{
			// only send if the volume has actually changed
			if (normalizedVolume != lastSentNormalizedVolume)
			{
				lastSentNormalizedVolume = normalizedVolume;

				// update local volume
				if (boombox.audioSource != null)
				{
					float actualVolume = normalizedVolume * boombox.maxVolumeLimit;
					//Logger.LogInfo($"Setting volume locally to {actualVolume}");
					boombox.audioSource.volume = actualVolume;
				}

				// update volume for all others too
				if (photonView != null)
				{
					photonView.RPC("UpdateVolume", RpcTarget.AllBuffered, normalizedVolume, PhotonNetwork.LocalPlayer.ActorNumber);
				}
			}
		}

		private void SendQualityUpdate()
		{
			// only send if the quality has actually changed
			if (qualityLevel != lastSentQualityLevel)
			{
				lastSentQualityLevel = qualityLevel;

				// update local quality
				if (boombox != null)
				{
					//Logger.LogInfo($"Setting quality locally to {qualityLevel}");
					boombox.SetQuality(qualityLevel);
				}

				// update wual for others too
				if (photonView != null)
				{
					photonView.RPC("UpdateQuality", RpcTarget.AllBuffered, qualityLevel, PhotonNetwork.LocalPlayer.ActorNumber);
				}
			}
		}

		public void HideUI()
		{
			if (showUI)
			{
				showUI = false;

				Cursor.lockState = previousLockMode;
				Cursor.visible = previousCursorVisible;

				//Logger.LogInfo("BoomboxUI hidden");
			}
		}

		public bool IsUIVisible()
		{
			return showUI;
		}

		public void UpdateStatus(string message)
		{
			statusMessage = message;
		}

		private void InitializeStyles()
		{
			if (stylesInitialized)
				return;

			backgroundTexture = CreateColorTexture(new Color(0.1f, 0.1f, 0.1f, 0.9f));
			buttonTexture = CreateColorTexture(new Color(0.2f, 0.2f, 0.3f, 1f));
			sliderBackgroundTexture = CreateColorTexture(new Color(0.15f, 0.15f, 0.2f, 1f));
			sliderThumbTexture = CreateColorTexture(new Color(0.7f, 0.7f, 0.8f, 1f));
			textFieldBackgroundTexture = CreateColorTexture(new Color(0.15f, 0.17f, 0.2f, 1f));

			windowStyle = new GUIStyle(GUI.skin.window);
			windowStyle.normal.background = backgroundTexture;
			windowStyle.onNormal.background = backgroundTexture;
			windowStyle.border = new RectOffset(10, 10, 10, 10);
			windowStyle.padding = new RectOffset(15, 15, 20, 15);

			headerStyle = new GUIStyle(GUI.skin.label);
			headerStyle.fontSize = 18;
			headerStyle.fontStyle = FontStyle.Bold;
			headerStyle.normal.textColor = Color.white;
			headerStyle.alignment = TextAnchor.MiddleCenter;
			headerStyle.margin = new RectOffset(0, 0, 10, 20);

			buttonStyle = new GUIStyle(GUI.skin.button);
			buttonStyle.normal.background = buttonTexture;
			buttonStyle.hover.background = CreateColorTexture(new Color(0.3f, 0.3f, 0.4f, 1f));
			buttonStyle.active.background = CreateColorTexture(new Color(0.4f, 0.4f, 0.5f, 1f));
			buttonStyle.normal.textColor = Color.white;
			buttonStyle.hover.textColor = Color.white;
			buttonStyle.active.textColor = Color.white;
			buttonStyle.fontSize = 14;
			buttonStyle.padding = new RectOffset(15, 15, 8, 8);
			buttonStyle.margin = new RectOffset(5, 5, 5, 5);
			buttonStyle.alignment = TextAnchor.MiddleCenter;

			smallButtonStyle = new GUIStyle(buttonStyle);
			smallButtonStyle.padding = new RectOffset(8, 8, 4, 4);
			smallButtonStyle.fontSize = 12;

			textFieldStyle = new GUIStyle(GUI.skin.textField);
			textFieldStyle.normal.background = textFieldBackgroundTexture;
			textFieldStyle.normal.textColor = new Color(1f, 1f, 1f);
			textFieldStyle.fontSize = 14;
			textFieldStyle.padding = new RectOffset(10, 10, 8, 8);

			scrollViewStyle = new GUIStyle(GUI.skin.scrollView);
			scrollViewStyle.normal.background = textFieldBackgroundTexture;
			scrollViewStyle.border = new RectOffset(2, 2, 2, 2);
			scrollViewStyle.padding = new RectOffset(0, 0, 0, 0);

			labelStyle = new GUIStyle(GUI.skin.label);
			labelStyle.normal.textColor = Color.white;
			labelStyle.fontSize = 14;
			labelStyle.margin = new RectOffset(0, 0, 10, 5);

			statusStyle = new GUIStyle(GUI.skin.label);
			statusStyle.normal.textColor = Color.cyan;
			statusStyle.fontSize = 14;
			statusStyle.wordWrap = true;
			statusStyle.alignment = TextAnchor.MiddleCenter;

			sliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
			sliderStyle.normal.background = sliderBackgroundTexture;

			stylesInitialized = true;
		}

		private Texture2D CreateColorTexture(Color color)
		{
			Texture2D texture = new Texture2D(1, 1);
			texture.SetPixel(0, 0, color);
			texture.Apply();
			return texture;
		}

		private void OnGUI()
		{
			if (!showUI)
				return;

			if (!stylesInitialized)
				InitializeStyles();

			windowRect = GUILayout.Window(0, windowRect, DrawUI, "Boombox Controller", windowStyle);
		}

		private void DrawUI(int windowID)
		{
			GUILayout.Label("Control The Boombox In The Cart", headerStyle);

			scrollPosition = GUILayout.BeginScrollView(scrollPosition, false, false, GUILayout.ExpandHeight(true));

			// URL Input Section

			GUILayout.Space(10);
			GUILayout.BeginHorizontal();
			GUILayout.Label("Enter Video URL:", labelStyle, GUILayout.ExpandWidth(true));

			if (GUILayout.Button("Clear", smallButtonStyle, GUILayout.Width(60)))
			{
				urlInput = "";
				GUI.FocusControl(null);
			}
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();

			urlScrollPosition = GUILayout.BeginScrollView(
				urlScrollPosition,
				false,
				false,
				GUILayout.Height(60)
			);

			urlInput = GUILayout.TextField(urlInput, textFieldStyle, GUILayout.Height(34));
			urlInput = Regex.Replace(urlInput, @"\s+", "");

			GUILayout.EndScrollView();
			GUILayout.EndHorizontal();

			// Volume Control Section

			GUILayout.Space(15);
			float displayPercentage = normalizedVolume * 100f;
			GUILayout.Label($"Volume: {Mathf.Round(displayPercentage)}%", labelStyle);

			GUILayout.BeginHorizontal();

			// check if the user started dragging the slider
			if (Event.current.type == EventType.MouseDown &&
				GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
			{
				isSliderBeingDragged = true;
			}

			float newNormalizedVolume = GUILayout.HorizontalSlider(normalizedVolume, 0f, 1f, sliderStyle, GUI.skin.horizontalSliderThumb);

			// if volume changed and we weren't already dragging, start tracking drag
			if (newNormalizedVolume != normalizedVolume && !isSliderBeingDragged)
			{
				isSliderBeingDragged = true;
			}

			normalizedVolume = newNormalizedVolume;

			// update local volume for immediate feedback while sliding
			if (boombox != null && boombox.audioSource != null)
			{
				float actualVolume = normalizedVolume * boombox.maxVolumeLimit;
				boombox.audioSource.volume = actualVolume;
			}

			GUILayout.EndHorizontal();

			// Quality Control Section

			GUILayout.Space(15);
			GUILayout.Label($"Audio Quality: {qualityLabels[qualityLevel]}", labelStyle);
			GUILayout.BeginHorizontal();

			// check if the user started dragging the slider
			if (Event.current.type == EventType.MouseDown &&
				GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
			{
				isQualitySliderBeingDragged = true;
			}

			float sliderValue = GUILayout.HorizontalSlider(qualityLevel, 0f, 4f, sliderStyle, GUI.skin.horizontalSliderThumb);
			int newQualityLevel = Mathf.RoundToInt(sliderValue);

			// if quality changed and we weren't already dragging, start tracking drag
			if (newQualityLevel != qualityLevel && !isQualitySliderBeingDragged)
			{
				isQualitySliderBeingDragged = true;
			}

			qualityLevel = newQualityLevel;

			// update local quality for immediate feedback while sliding
			if (boombox != null)
			{
				boombox.SetQuality(qualityLevel);
			}

			GUILayout.EndHorizontal();

			// Status Message Display

			GUILayout.Space(15);
			if (!string.IsNullOrEmpty(statusMessage))
			{
				GUILayout.Label(statusMessage, statusStyle);
				GUILayout.Space(5);
			}


			// Error Message Display

			if (!string.IsNullOrEmpty(errorMessage))
			{
				GUI.color = Color.red;
				GUILayout.Label(errorMessage, labelStyle);
				GUI.color = Color.white;
				GUILayout.Space(5);
			}

			// Main Control Buttons

			GUILayout.Space(10);
			GUILayout.BeginHorizontal();
			GUI.enabled = boombox != null && !boombox.IsDownloadInProgress();
			if (GUILayout.Button("▶ PLAY", buttonStyle, GUILayout.Height(40)))
			{
				if (IsValidVideoUrl(urlInput))
				{
					if (PhotonNetwork.IsConnected)
					{
						photonView.RPC("RequestSong", RpcTarget.All, urlInput, PhotonNetwork.LocalPlayer.ActorNumber);
					}
					else
					{
						//singleplayer
						boombox.RequestSong(urlInput, PhotonNetwork.LocalPlayer.ActorNumber);
					}
					GUI.FocusControl(null);
				}
				else
				{
					ShowErrorMessage("Invalid Video URL!");
				}
			}
			GUI.enabled = true;

			GUI.enabled = boombox != null && boombox.isPlaying;
			if (GUILayout.Button("\u25A0 STOP", buttonStyle, GUILayout.Height(40)))
			{
				if (PhotonNetwork.IsConnected)
				{
					if (PhotonNetwork.IsConnected)
					{
						photonView.RPC("StopPlayback", RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber);
					}
					else
					{
						//singleplayer
						boombox.StopPlayback(PhotonNetwork.LocalPlayer.ActorNumber);
					}
				}
				else
				{
					//singleplayer
					boombox.StopPlayback(PhotonNetwork.LocalPlayer.ActorNumber);
				}

				GUI.FocusControl(null);
			}
			GUI.enabled = true;
			GUILayout.EndHorizontal();

			// Download Status Information

			if (boombox != null && boombox.IsDownloadInProgress())
			{
				GUILayout.Space(10);
				GUILayout.Label("Download in progress...", statusStyle);
			}

			GUILayout.EndScrollView();

			// Close Button

			GUILayout.Space(10);
			GUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("Close", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
			{
				if (!PhotonNetwork.IsConnected)
				{
					// singleplayer
					HideUI();
				}
				if (controller != null)
				{
					controller.ReleaseControl();
				}
				else
				{
					HideUI();
				}
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();

				historyScrollPosition = GUILayout.BeginScrollView(historyScrollPosition, false, false, GUILayout.ExpandHeight(true));
			GUILayout.FlexibleSpace();
			if (Boombox.historyEntries.Length > 0)
			{
				GUILayout.Label("Recently Played:", labelStyle);
			}
			for (int i = 0; i < Boombox.historyEntries.Length; i++)
			{
				int index = Boombox.historyEntries.Length - 1 - i;
				HistoryEntry entry = Boombox.historyEntries[index];
				if (GUILayout.Button("▶  " + entry.title, smallButtonStyle, GUILayout.Height(30)))
				{
					urlInput = entry.url;

					GUI.FocusControl(null);
					if (IsValidVideoUrl(urlInput))
					{
						if (PhotonNetwork.IsConnected)
						{
							photonView.RPC("RequestSong", RpcTarget.All, urlInput, PhotonNetwork.LocalPlayer.ActorNumber);
						}
						else
						{
							//singleplayer
							boombox.RequestSong(urlInput, PhotonNetwork.LocalPlayer.ActorNumber);
						}
					}
					else
					{
						ShowErrorMessage("Invalid Video URL from history!");
					}
				}
			}
			if (Boombox.historyEntries.Length > 0)
			{
				if (GUILayout.Button("Clear History", buttonStyle, GUILayout.Height(30)))
				{
					Boombox.clearHistory();
				}
			}
			GUILayout.EndScrollView();	
			
			
			GUI.DragWindow(new Rect(0, 0, windowRect.width, 30));
		}

		private bool IsValidVideoUrl(string url)
		{
			return Boombox.IsValidVideoUrl(url);
		}

		private void ShowErrorMessage(string message)
		{
			Debug.LogError(message);
			errorMessage = message;
			errorMessageTime = Time.time + 3f;
		}

		private void OnDestroy()
		{
			if (backgroundTexture != null) Destroy(backgroundTexture);
			if (buttonTexture != null) Destroy(buttonTexture);
			if (sliderBackgroundTexture != null) Destroy(sliderBackgroundTexture);
			if (sliderThumbTexture != null) Destroy(sliderThumbTexture);
		}
	}
}