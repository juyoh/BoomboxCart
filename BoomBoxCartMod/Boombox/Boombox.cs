using Photon.Pun;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Networking;
using System;
using System.IO;
using BepInEx.Logging;
using System.Text.RegularExpressions;
using System.Collections;
using System.Linq;
using Photon.Realtime;
using System.Xml;

namespace BoomBoxCartMod
{
	public class Boombox : MonoBehaviourPunCallbacks
	{
		private static BoomBoxCartMod Instance = BoomBoxCartMod.instance;
		private static ManualLogSource Logger => Instance.logger;

		public PhotonView photonView;
		public AudioSource audioSource;

		public float maxVolumeLimit = 0.8f; // make the range smaller than 0 to 1.0 volume!
		float minDistance = 3f;
		float maxDistanceBase = 10f;
		float maxDistanceAddition = 20f;

		private static bool isDownloadInProgress = false;
		private static string currentDownloadUrl = "";
		private static string currentRequestId = "";

		public string currentSongUrl = "";
		public string currentSongTitle = "No song playing";
		private bool isAwaitingSyncPlayback = false;
		public bool isPlaying = false;
		private bool isTimeoutRecovery = false;

		private AudioLowPassFilter lowPassFilter;
		public int qualityLevel = 3; // 0 lowest, 3 highest

		// caches the AudioClips in memory using the URL as the key.
		private static Dictionary<string, AudioClip> downloadedClips = new Dictionary<string, AudioClip>();

		// cache for song titles
		private static Dictionary<string, string> songTitles = new Dictionary<string, string>();

		// tracks players ready and errors during/after download phase
		private static Dictionary<string, HashSet<int>> downloadsReady = new Dictionary<string, HashSet<int>>();
		private static Dictionary<string, HashSet<int>> downloadErrors = new Dictionary<string, HashSet<int>>();

		private const float DOWNLOAD_TIMEOUT = 40f; // 40 seconds timeout for downloads
		private Dictionary<string, Coroutine> timeoutCoroutines = new Dictionary<string, Coroutine>();

		// all valid URL's to donwload audio from
		private static readonly Regex[] supportedVideoUrlRegexes = new[]
		{
		// YouTube URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www|m)\.)?((?:youtube(?:-nocookie)?\.com|youtu\.be))(\/(?:[\w\-]+\?v=|embed\/|live\/|v\/)?)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    
		// RuTube URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www)?\.?)(rutube\.ru)(\/video\/)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    
		// Yandex Music URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www)?\.?)(music\.yandex\.ru)(\/album\/\d+\/track\/)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    
		// Bilibili URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www|m)\.)?(bilibili\.com)(\/video\/)([\w\-]+)(\S+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),

		// SoundCloud URLs
		new Regex(@"^((?:https?:)?\/\/)?((?:www|m)\.)?(soundcloud\.com|snd\.sc)\/([\w\-]+\/[\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)
		};
		private static readonly string historyFilePath = Path.Combine(Directory.GetCurrentDirectory(), "BoomboxedCart/history.txt");
		public static HistoryEntry[] historyEntries = new HistoryEntry[0];

		private void Awake()
		{
			audioSource = gameObject.AddComponent<AudioSource>();
			audioSource.volume = 0.15f;
			audioSource.spatialBlend = 1f;
			audioSource.playOnAwake = false;
			//audioSource.minDistance = 3f;
			//audioSource.maxDistance = 13f;
			//AnimationCurve curve = new AnimationCurve(
			//	new Keyframe(0f, 1f), // full volume at 0 distance
			//	//new Keyframe(3f, 0.8f),
			//	new Keyframe(6f, 1f),
			//	new Keyframe(10f, 0.5f),
			//	new Keyframe(13f, 0f) // fully silent at maxDistance
			//);
			//audioSource.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
			audioSource.rolloffMode = AudioRolloffMode.Custom;
			audioSource.spread = 90f;
			audioSource.dopplerLevel = 0f;
			audioSource.reverbZoneMix = 1f;
			audioSource.spatialize = true;
			lowPassFilter = gameObject.AddComponent<AudioLowPassFilter>();
			lowPassFilter.enabled = false;

			UpdateAudioRangeBasedOnVolume(audioSource.volume);

			//Logger.LogInfo($"AudioSource: {audioSource}");
			photonView = GetComponent<PhotonView>();
			//Logger.LogInfo($"PhotonView: {photonView}");

			isAwaitingSyncPlayback = false;

			if (photonView == null)
			{
				Logger.LogError("PhotonView not found on Boombox object.");
				return;
			}

			// add BoomboxController component for handling temporary ownership
			if (GetComponent<BoomboxController>() == null)
			{
				gameObject.AddComponent<BoomboxController>();
				//Logger.LogInfo("BoomboxController component added to Boombox");
			}

			Logger.LogInfo($"Boombox initialized on this cart. AudioSource: {audioSource}, PhotonView: {photonView}");
			loadHistory();
		}

		private void Update()
		{
			// try to prevent double playing for the player(s) who downloaded song, but waiting for slowest to sync w
			if (isAwaitingSyncPlayback && audioSource.isPlaying)
			{
				audioSource.Stop();
				isPlaying = false;
				//Logger.LogInfo("Stopped premature audio playback while waiting for sync");
			}
		}

			private void OnDestroy()
			{
				foreach (var coroutine in timeoutCoroutines.Values)
				{
					if (coroutine != null)
					{
						StopCoroutine(coroutine);
					}
				}
				timeoutCoroutines.Clear();
			}

		public bool IsDownloadInProgress()
		{
			return isDownloadInProgress;
		}

		public string GetCurrentDownloadUrl()
		{
			return currentDownloadUrl;
		}

		public static bool IsValidVideoUrl(string url)
		{
			return !string.IsNullOrWhiteSpace(url) && supportedVideoUrlRegexes.Any(regex => regex.IsMatch(url));
		}

		[PunRPC]
		public async void RequestSong(string url, int requesterId)
		{
			//Logger.LogInfo($"RequestSong RPC received: url={url}, requesterId={requesterId}");

			if (!IsValidVideoUrl(url))
			{
				Logger.LogError($"Invalid Video URL: {url}");
				if (requesterId == PhotonNetwork.LocalPlayer.ActorNumber)
				{
					UpdateUIStatus("Error: Invalid Video URL.");
				}
				return;
			}

			// requests get unique ID's 
			string requestId = Guid.NewGuid().ToString();

			// only process if we're not already downloading smthn (i tihnk greyed out play button stops this)
			if (isDownloadInProgress && requesterId != PhotonNetwork.MasterClient.ActorNumber)
			{
				//Logger.LogWarning($"Download already in progress. Ignoring request from {requesterId}");
				if (requesterId == PhotonNetwork.LocalPlayer.ActorNumber)
				{
					UpdateUIStatus("Please wait for the current download to complete.");
				}
				return;
			}

			isDownloadInProgress = true;
			isAwaitingSyncPlayback = true;
			currentDownloadUrl = url;
			currentRequestId = requestId;

			// try to fix double playing
			CleanupCurrentPlayback();

			timeoutCoroutines[requestId] = StartCoroutine(DownloadTimeoutCoroutine(requestId, url));

			if (requesterId == PhotonNetwork.LocalPlayer.ActorNumber)
			{
				UpdateUIStatus($"Downloading audio from {url}...");
			}

			if (downloadsReady.ContainsKey(url))
				downloadsReady[url].Clear();
			else
				downloadsReady[url] = new HashSet<int>();

			if (downloadErrors.ContainsKey(url))
				downloadErrors[url].Clear();
			else
				downloadErrors[url] = new HashSet<int>();

			// download clip (and title) if it's not cached already
			if (!downloadedClips.ContainsKey(url))
			{
				try
				{
					var (filePath, title) = await YoutubeDL.DownloadAudioWithTitleAsync(url);


					addToHistory(url, songTitles.ContainsKey(url) ? songTitles[url] : title);

					songTitles[url] = title;
					photonView.RPC("SetSongTitle", RpcTarget.AllBuffered, url, title);
					//Logger.LogInfo($"Set song title for url: {url} to {title}");

					if (requesterId == PhotonNetwork.LocalPlayer.ActorNumber)
					{
						UpdateUIStatus($"Processing audio: {title}");
					}

					AudioClip clip = await GetAudioClipAsync(filePath);

					downloadedClips[url] = clip;
					Logger.LogInfo($"Downloaded and cached clip for video: {title}");
				}
				catch (Exception ex)
				{
					//Logger.LogError($"Failed to download audio: {ex.Message}");

					if (requesterId == PhotonNetwork.LocalPlayer.ActorNumber)
					{
						UpdateUIStatus($"Error: {ex.Message}");
					}

					// Report download error to other players
					photonView.RPC("ReportDownloadError", RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber, url, ex.Message);

					isAwaitingSyncPlayback = false;
					return;
				}
			}
			else
			{
				//Logger.LogInfo($"Clip already cached for url: {url}");
			}

			// report that this player has completed the download
			photonView.RPC("ReportDownloadComplete", RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber, url);

			if (requesterId == PhotonNetwork.LocalPlayer.ActorNumber)
			{
				UpdateUIStatus("Waiting for all players to be ready...");
			}

			// Only the requester (or master client if handling a timeout) should wait and initiate playback
			if ((requesterId == PhotonNetwork.LocalPlayer.ActorNumber) ||
				(PhotonNetwork.IsMasterClient && (requesterId != PhotonNetwork.LocalPlayer.ActorNumber || isTimeoutRecovery)))
			{
				//Logger.LogInfo("Waiting for all players to be ready for playback...");
				if (PhotonNetwork.IsConnected)
				{
					await WaitForPlayersReadyOrFailed(url);
				}

				// check if current request ID is still valid (prob wont happen)
				if (currentRequestId != requestId)
				{
					Logger.LogWarning($"Request ID mismatch. Current: {currentRequestId}, This request: {requestId}");
					return;
				}

				// notify for players that errored, but still play for others
				if (downloadErrors.ContainsKey(url) && downloadErrors[url].Count > 0)
				{
					string errorMessage = $"Some players had download errors. Continuing playback for {downloadsReady[url].Count} players.";
					Logger.LogWarning(errorMessage);
					photonView.RPC("NotifyPlayersOfErrors", RpcTarget.All, errorMessage);
				}

				Logger.LogInfo($"All players ready for playback. Initiating sync playback for {downloadsReady[url].Count} players.");
				if (PhotonNetwork.IsConnected)
				{
					photonView.RPC("SyncPlayback", RpcTarget.All, url, requesterId);
				} else
				{
					// singleplayer
					SyncPlayback(url, requesterId);
				}
				

			}

			if (timeoutCoroutines.TryGetValue(requestId, out Coroutine coroutine) && coroutine != null)
			{
				StopCoroutine(coroutine);
				timeoutCoroutines.Remove(requestId);
			}

			if (currentRequestId == requestId)
			{
				isDownloadInProgress = false;
				currentDownloadUrl = "";
				currentRequestId = "";
			}
		}

		[PunRPC]
		public void NotifyPlayersOfErrors(string message)
		{
			Logger.LogWarning(message);
			UpdateUIStatus(message);
		}

		[PunRPC]
		public void ReportDownloadError(int actorNumber, string url, string errorMessage)
		{

			isDownloadInProgress = false;
			Logger.LogError($"Player {actorNumber} reported download error for {url}: {errorMessage}");

			if (!downloadErrors.ContainsKey(url))
				downloadErrors[url] = new HashSet<int>();

			downloadErrors[url].Add(actorNumber);

			if (actorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
			{
				
				UpdateUIStatus($"Error: {errorMessage}");
			}
		}

		private IEnumerator DownloadTimeoutCoroutine(string requestId, string url)
		{
			yield return new WaitForSeconds(DOWNLOAD_TIMEOUT);

			if (currentRequestId == requestId && isDownloadInProgress)
			{
				Logger.LogWarning($"Download timeout for url: {url}");

				if (PhotonNetwork.IsMasterClient)
				{
					Logger.LogInfo("Master client initiating timeout recovery");
					isTimeoutRecovery = true;

					foreach (var player in PhotonNetwork.PlayerList)
					{
						if (!downloadsReady[url].Contains(player.ActorNumber))
						{
							if (!downloadErrors.ContainsKey(url))
								downloadErrors[url] = new HashSet<int>();

							downloadErrors[url].Add(player.ActorNumber);
							Logger.LogWarning($"Player {player.ActorNumber} timed out during download");
						}
					}

					if (downloadsReady.ContainsKey(url) && downloadsReady[url].Count > 0)
					{
						string timeoutMessage = $"Some players timed out. Continuing playback for {downloadsReady[url].Count} players.";
						photonView.RPC("NotifyPlayersOfErrors", RpcTarget.All, timeoutMessage);

						// initiate playback with the master client as requester to unblock the system
						photonView.RPC("SyncPlayback", RpcTarget.All, url, PhotonNetwork.LocalPlayer.ActorNumber);
					}
					else
					{
						photonView.RPC("NotifyPlayersOfErrors", RpcTarget.All, "Download timed out for all players.");
					}

					isDownloadInProgress = false;
					currentDownloadUrl = "";
					currentRequestId = "";
					isTimeoutRecovery = false;
				}
			}

			timeoutCoroutines.Remove(requestId);
		}

		[PunRPC]
		public void SetSongTitle(string url, string title)
		{
			// cache title
			songTitles[url] = title;

			if (currentSongUrl == url)
			{
				currentSongTitle = title;
				UpdateUIStatus($"Now playing: {title}");
			}
		}

		[PunRPC]
		public void ReportDownloadComplete(int actorNumber, string url)
		{
			if (!downloadsReady.ContainsKey(url))
				downloadsReady[url] = new HashSet<int>();

			downloadsReady[url].Add(actorNumber);
			//Logger.LogInfo($"Player {actorNumber} reported ready for url: {url}. Total ready: {downloadsReady[url].Count}");
		}

		private async Task WaitForPlayersReadyOrFailed(string url)
		{
			int totalPlayers = PhotonNetwork.PlayerList.Length;
			int readyCount = 0;
			int errorCount = 0;

			// Wait until either all players are accounted for (ready + error = total) 
			// or we have at least one player ready and some have errors
			while (true)
			{
				readyCount = downloadsReady.ContainsKey(url) ? downloadsReady[url].Count : 0;
				errorCount = downloadErrors.ContainsKey(url) ? downloadErrors[url].Count : 0;

				//Logger.LogInfo($"{PhotonNetwork.LocalPlayer.ActorNumber}: Waiting for players to be ready. Ready: {readyCount}, Errors: {errorCount}, Total: {totalPlayers}");

				// Exit conditions:
				// 1. All players are accounted for (ready + errors = total)
				// 2. At least one player is ready and some have errors and we've waited a reasonable time
				if (readyCount + errorCount >= totalPlayers ||
					(readyCount > 0 && errorCount > 0 && await Task.Delay(5000).ContinueWith(_ => true)))
				{
					break;
				}

				await Task.Delay(100);
			}

			Logger.LogInfo($"Ready to proceed with playback. Ready: {readyCount}, Errors: {errorCount}, Total: {totalPlayers}");
		}

		[PunRPC]
		public void SyncPlayback(string url, int requesterId)
		{
			//Logger.LogInfo($"SyncPlayback RPC received: url={url}, requesterId={requesterId}");

			if (isPlaying && currentSongUrl == url && audioSource.isPlaying)
			{
				//Logger.LogInfo($"Already playing {url}, ignoring duplicate SyncPlayback");
				return;
			}

			// check if this player has the song downloaded
			if (!downloadedClips.ContainsKey(url))
			{
				Logger.LogError($"Clip not found for url: {url}");
				return;
			}

			CleanupCurrentPlayback();

			currentSongUrl = url;
			if (songTitles.TryGetValue(url, out string title))
			{
				currentSongTitle = title;
			}

			isAwaitingSyncPlayback = false;

			// play that thang
			audioSource.clip = downloadedClips[url];
			SetQuality(qualityLevel);
			UpdateAudioRangeBasedOnVolume(audioSource.volume);
			audioSource.Play();
			isPlaying = true;

			UpdateUIStatus($"Now playing: {currentSongTitle}");
		}

		private void CleanupCurrentPlayback()
		{
			if (audioSource.isPlaying)
			{
				audioSource.Stop();
				isPlaying = false;
			}

			audioSource.clip = null;
		}

		public void RemoveFromCache(string url)
		{
			if (downloadedClips.ContainsKey(url))
			{
				AudioClip clip = downloadedClips[url];
				downloadedClips.Remove(url);

				if (clip != null)
				{
					Destroy(clip);
				}

				//Logger.LogInfo($"Removed clip for url: {url} from cache");
			}

			if (songTitles.ContainsKey(url))
			{
				songTitles.Remove(url);
			}

			if (downloadsReady.ContainsKey(url))
			{
				downloadsReady.Remove(url);
			}

			if (downloadErrors.ContainsKey(url))
			{
				downloadErrors.Remove(url);
			}
		}

		public void SetQuality(int level)
		{
			qualityLevel = Mathf.Clamp(level, 0, 4);

			switch (qualityLevel)
			{
				case 0: // hella low
					lowPassFilter.enabled = true;
					lowPassFilter.cutoffFrequency = 1500f;
					break;
				case 1: // low quality
					lowPassFilter.enabled = true;
					lowPassFilter.cutoffFrequency = 3000f;
					break;
				case 2: // medium-low quality
					lowPassFilter.enabled = true;
					lowPassFilter.cutoffFrequency = 4500f;
					break;
				case 3: // medium-high (default rn)
					lowPassFilter.enabled = true;
					lowPassFilter.cutoffFrequency = 6000f;
					break;
				case 4: // highest quality
					lowPassFilter.enabled = false;
					break;
			}

			//Logger.LogInfo($"Audio quality set to level {qualityLevel}");
		}

		[PunRPC]
		public void UpdateQuality(int level, int requesterId)
		{
			BoomboxController controller = GetComponent<BoomboxController>();

			SetQuality(level);
			//Logger.LogInfo($"Quality updated to level {level} by player {requesterId}");
		}


		[PunRPC]
		public void UpdateVolume(float volume, int requesterId)
		{
			BoomboxController controller = GetComponent<BoomboxController>();

			float actualVolume = volume * maxVolumeLimit;
			audioSource.volume = actualVolume;
			UpdateAudioRangeBasedOnVolume(actualVolume);
			//Logger.LogInfo($"Volume updated to {actualVolume} by player {requesterId}");
		}

		private void UpdateAudioRangeBasedOnVolume(float volume)
		{
			// louder volume = hear from farther away
			float newMaxDistance = Mathf.Lerp(maxDistanceBase, maxDistanceBase + maxDistanceAddition, volume); // More noticeable effect

			audioSource.minDistance = minDistance;
			audioSource.maxDistance = newMaxDistance;

			AnimationCurve curve = new AnimationCurve(
				new Keyframe(0f, 1f),
				new Keyframe(minDistance, 0.9f),
				new Keyframe(newMaxDistance, 0f)
			);

			audioSource.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);

			//Logger.LogInfo($"Updated audio range based on volume {volume}: maxDistance={newMaxDistance}");
		}

		[PunRPC]
		public void PausePlayback(int requesterId)
		{
			if (audioSource.isPlaying)
			{
				audioSource.Pause();
				isPlaying = false;
				//Logger.LogInfo($"Playback paused by player {requesterId}");
				UpdateUIStatus($"Paused: {currentSongTitle}");
			}
		}

		[PunRPC]
		public void StopPlayback(int requesterId)
		{
			if (audioSource.isPlaying)
			{
				audioSource.Stop();
				isPlaying = false;
				//Logger.LogInfo($"Playback stopped by player {requesterId}");
				UpdateUIStatus($"Stopped: {currentSongTitle}");
			}
		}

		private void UpdateUIStatus(string message)
		{
			BoomboxUI ui = GetComponent<BoomboxUI>();
			if (ui != null && ui.IsUIVisible())
			{
				ui.UpdateStatus(message);
			}
		}
		

		public override void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
		{
			// idek if this method works, never tested, but its here !
			if (isDownloadInProgress)
			{
				if (!string.IsNullOrEmpty(currentDownloadUrl))
				{
					if (downloadsReady.ContainsKey(currentDownloadUrl) &&
						!downloadsReady[currentDownloadUrl].Contains(otherPlayer.ActorNumber))
					{
						if (!downloadErrors.ContainsKey(currentDownloadUrl))
							downloadErrors[currentDownloadUrl] = new HashSet<int>();

						downloadErrors[currentDownloadUrl].Add(otherPlayer.ActorNumber);
						Logger.LogInfo($"Player {otherPlayer.ActorNumber} left during download - marking as error");
					}
				}
			}

			base.OnPlayerLeftRoom(otherPlayer);
		}

		public override void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
		{
			// no idea if this works, but ancitipating a late join mod

			base.OnPlayerEnteredRoom(newPlayer);

			if (PhotonNetwork.IsMasterClient && isPlaying && !string.IsNullOrEmpty(currentSongUrl))
			{
				Logger.LogInfo($"New player {newPlayer.ActorNumber} joined - syncing current playback state");

				if (songTitles.ContainsKey(currentSongUrl))
				{
					photonView.RPC("SetSongTitle", newPlayer, currentSongUrl, songTitles[currentSongUrl]);
				}

				photonView.RPC("SyncPlayback", newPlayer, currentSongUrl, PhotonNetwork.LocalPlayer.ActorNumber);
				photonView.RPC("UpdateQuality", newPlayer, qualityLevel, PhotonNetwork.LocalPlayer.ActorNumber);

				// somehow get new player to acc play the song for themselves too
			}
		}

		public static async Task<AudioClip> GetAudioClipAsync(string filePath)
		{
			if (!File.Exists(filePath))
			{
				throw new Exception($"Audio file not found at path: {filePath}");
			}

			//string escapedPath = Uri.EscapeDataString(filePath);
			//string uri = "file:///" + escapedPath.Replace("%5C", "/").Replace("%3A", ":");

			Uri fileUri = new Uri(filePath);
			string uri = fileUri.AbsoluteUri;

			//Logger.LogInfo($"Loading audio clip from: {uri}");

			using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
			{
				www.timeout = (int) DOWNLOAD_TIMEOUT;

				TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
				var operation = www.SendWebRequest();
				operation.completed += (asyncOp) =>
				{
					if (www.result != UnityWebRequest.Result.Success)
					{
						Logger.LogError($"Web request failed: {www.error}, URI: {uri}");
						tcs.SetException(new Exception($"Failed to load audio file: {www.error}"));
					}
					else
						tcs.SetResult(true);
				};
				await tcs.Task;
				AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

				if (clip != null)
				{
					try
					{
						await Task.Delay(500);
						if (Directory.Exists(Path.GetDirectoryName(filePath)))
						{
							Directory.Delete(Path.GetDirectoryName(filePath), true);
						}
					}
					catch (Exception ex)
					{
						Logger.LogWarning($"Failed to clean up temp directory: {ex.Message}");
					}
				}

				return clip;
			}
		}
		public static bool hasHistory()
		{
			return historyEntries != null && historyEntries.Length > 0;
		}
		public static void addToHistory(string url, string title)
		{
			if (historyEntries.Any(entry => entry.url == url))
			{
				return;
			}
			HistoryEntry newEntry = new HistoryEntry(url, title);
			historyEntries = historyEntries.Append(newEntry).ToArray();

			// enforce max history size of 10
			if (historyEntries.Length > 10)
			{
				historyEntries = historyEntries.Skip(historyEntries.Length - 10).ToArray();
			}

			saveHistory();
		}
		public static void clearHistory()
		{
			historyEntries = new HistoryEntry[0];
			saveHistory();
		}
		public static void loadHistory()
		{
			if (File.Exists(historyFilePath))
			{
				FileStream fs = new FileStream(historyFilePath, FileMode.Open, FileAccess.Read);
				XmlReader xmlReader = XmlReader.Create(fs);
				xmlReader.ReadToFollowing("HistoryEntries");
				if (xmlReader.ReadToDescendant("HistoryEntry"))
				{
					List<HistoryEntry> newEntries = new List<HistoryEntry>();
					do
					{
						string url = xmlReader.GetAttribute("Url");
						string title = xmlReader.GetAttribute("Title");
						if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(title))
						{
							newEntries.Add(new HistoryEntry(url, title));
						}
					} while (xmlReader.ReadToNextSibling("HistoryEntry"));
					historyEntries = newEntries.ToArray();
				}
				else
				{
					Logger.LogInfo("history.txt is empty!");
				}
			}
		}
		public static void saveHistory()
		{
			using (FileStream fs = new FileStream(historyFilePath, FileMode.Create, FileAccess.Write))
			using (XmlWriter xmlWriter = XmlWriter.Create(fs, new XmlWriterSettings { Indent = true }))
			{
				xmlWriter.WriteStartDocument();
				xmlWriter.WriteStartElement("HistoryEntries");
				foreach (var entry in historyEntries)
				{
					xmlWriter.WriteStartElement("HistoryEntry");
					xmlWriter.WriteAttributeString("Url", entry.url);
					xmlWriter.WriteAttributeString("Title", entry.title);
					xmlWriter.WriteEndElement();
				}
				xmlWriter.WriteEndElement();
				xmlWriter.WriteEndDocument();
			}
			Logger.LogInfo($"Saved {historyEntries.Length} history entries to {historyFilePath}");
		}
		
	}
}
