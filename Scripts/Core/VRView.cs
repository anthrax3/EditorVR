#if UNITY_EDITOR && UNITY_EDITORVR
using System;
using UnityEngine;
using UnityEngine.Assertions;
using System.Collections;
using System.Collections.Generic;
using UnityEditor.Experimental.EditorVR.Helpers;
using System.Reflection;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEngine.VR;
using UnityEngine.InputNew;

#if ENABLE_STEAMVR_INPUT
using Valve.VR;
#endif
using Object = UnityEngine.Object;

namespace UnityEditor.Experimental.EditorVR.Core
{
	[InitializeOnLoad]
	sealed class VRView : EditorWindow
	{
		const string k_ShowDeviceView = "VRView.ShowDeviceView";
		const string k_UseCustomPreviewCamera = "VRView.UseCustomPreviewCamera";
		const string k_LaunchOnExitPlaymode = "VRView.LaunchOnExitPlaymode";
		const float k_HMDActivityTimeout = 3f; // in seconds

		DrawCameraMode m_RenderMode = DrawCameraMode.Textured;

		// To allow for alternate previews (e.g. smoothing)
		public static Camera customPreviewCamera
		{
			set
			{
				if (s_ActiveView)
					s_ActiveView.m_CustomPreviewCamera = value;
			}
			get
			{
				return s_ActiveView && s_ActiveView.m_UseCustomPreviewCamera ?
					s_ActiveView.m_CustomPreviewCamera : null;
			}
		}
		Camera m_CustomPreviewCamera;

		[NonSerialized]
		private Camera m_Camera;

		LayerMask? m_CullingMask;
		private RenderTexture m_SceneTargetTexture;
		private bool m_ShowDeviceView;
		private bool m_SceneViewsEnabled;

		private static VRView s_ActiveView;

		private Transform m_CameraRig;
		private Quaternion m_LastHeadRotation = Quaternion.identity;
		private float m_TimeSinceLastHMDChange;
		private bool m_LatchHMDValues;

		bool m_HMDReady;
		bool m_VRInitialized;
		bool m_UseCustomPreviewCamera;

		public static Transform cameraRig
		{
			get
			{
				if (s_ActiveView)
				{
					return s_ActiveView.m_CameraRig;
				}

				return null;
			}
		}

		public static Camera viewerCamera
		{
			get
			{
				if (s_ActiveView)
				{
					return s_ActiveView.m_Camera;
				}

				return null;
			}
		}

		public static Rect rect
		{
			get
			{
				if (s_ActiveView)
				{
					return s_ActiveView.position;
				}

				return new Rect();
			}
		}

		public static VRView activeView
		{
			get
			{
				return s_ActiveView;
			}
		}

		public static bool showDeviceView
		{
			get { return s_ActiveView && s_ActiveView.m_ShowDeviceView; }
		}

		public static LayerMask cullingMask
		{
			set
			{
				if (s_ActiveView)
					s_ActiveView.m_CullingMask = value;
			}
		}

		public static event Action<EditorWindow> onGUIDelegate;
		public static event Action onHMDReady;

		public static VRView GetWindow()
		{
			return EditorWindow.GetWindow<VRView>(true);
		}

		public static Coroutine StartCoroutine(IEnumerator routine)
		{
			if (s_ActiveView && s_ActiveView.m_CameraRig)
			{
				var mb = s_ActiveView.m_CameraRig.GetComponent<EditorMonoBehaviour>();
				return mb.StartCoroutine(routine);
			}

			return null;
		}

		// Life cycle management across playmode switches is an odd beast indeed, and there is a need to reliably relaunch
		// EditorVR after we switch back out of playmode (assuming the view was visible before a playmode switch). So,
		// we watch until playmode is done and then relaunch.  
		static VRView()
		{
			EditorApplication.update += ReopenOnExitPlaymode;
		}

		private static void ReopenOnExitPlaymode()
		{
			bool launch = EditorPrefs.GetBool(k_LaunchOnExitPlaymode, false);
			if (!launch || !EditorApplication.isPlaying)
			{
				EditorPrefs.DeleteKey(k_LaunchOnExitPlaymode);
				EditorApplication.update -= ReopenOnExitPlaymode;
				if (launch)
					GetWindow();
			}
		}

		public void OnEnable()
		{
			EditorApplication.playmodeStateChanged += OnPlaymodeStateChanged;

			Assert.IsNull(s_ActiveView, "Only one EditorVR should be active");

			autoRepaintOnSceneChange = true;
			s_ActiveView = this;

			GameObject cameraGO = EditorUtility.CreateGameObjectWithHideFlags("VRCamera", HideFlags.HideAndDontSave, typeof(Camera));
			m_Camera = cameraGO.GetComponent<Camera>();
			m_Camera.useOcclusionCulling = false;
			m_Camera.enabled = false;
			m_Camera.cameraType = CameraType.VR;

			GameObject rigGO = EditorUtility.CreateGameObjectWithHideFlags("VRCameraRig", HideFlags.HideAndDontSave, typeof(EditorMonoBehaviour));
			m_CameraRig = rigGO.transform;
			m_Camera.transform.parent = m_CameraRig;
			m_Camera.nearClipPlane = 0.01f;
			m_Camera.farClipPlane = 1000f;

			// Generally, we want to be at a standing height, so default to that
			const float kHeadHeight = 1.7f;
			Vector3 position = m_CameraRig.position;
			position.y = kHeadHeight;
			m_CameraRig.position = position;
			m_CameraRig.rotation = Quaternion.identity;

			m_ShowDeviceView = EditorPrefs.GetBool(k_ShowDeviceView, false);
			m_UseCustomPreviewCamera = EditorPrefs.GetBool(k_UseCustomPreviewCamera, false);

			// Disable other views to increase rendering performance for EditorVR
			SetOtherViewsEnabled(false);

			VRSettings.StartRenderingToDevice();
			InputTracking.Recenter();
			// HACK: Fix VRSettings.enabled or some other API to check for missing HMD
			m_VRInitialized = false;
#if ENABLE_OVR_INPUT
			m_VRInitialized |= OVRPlugin.initialized;
#endif

#if ENABLE_STEAMVR_INPUT
			m_VRInitialized |= (OpenVR.IsHmdPresent() && OpenVR.Compositor != null);
#endif
			InitializeInputManager();
		}

		public void OnDisable()
		{
			GameObject currentContext;
			while (FindCurrentContext(out currentContext))
			{
				PopEditingContext();
			}
			ObjectUtils.Destroy(s_InputManager.gameObject);

			EditorApplication.playmodeStateChanged -= OnPlaymodeStateChanged;

			VRSettings.StopRenderingToDevice();

			EditorPrefs.SetBool(k_ShowDeviceView, m_ShowDeviceView);
			EditorPrefs.SetBool(k_UseCustomPreviewCamera, m_UseCustomPreviewCamera);

			SetOtherViewsEnabled(true);

			if (m_CameraRig)
				DestroyImmediate(m_CameraRig.gameObject, true);

			Assert.IsNotNull(s_ActiveView, "EditorVR should have an active view");
			s_ActiveView = null;
		}

		private void UpdateCamera()
		{
			// Latch HMD values early in case it is used in other scripts
			Vector3 headPosition = InputTracking.GetLocalPosition(VRNode.Head);
			Quaternion headRotation = InputTracking.GetLocalRotation(VRNode.Head);

			// HACK: Until an actual fix is found, this is a workaround
			// Delay until the VR subsystem has set the initial tracking position, then we can start latching values for
			// the HMD for the camera transform. Otherwise, we will bork the original centering of the HMD.
			var cameraTransform = m_Camera.transform;
			if (!Mathf.Approximately(Quaternion.Angle(cameraTransform.localRotation, Quaternion.identity), 0f))
				m_LatchHMDValues = true;

			if (Quaternion.Angle(headRotation, m_LastHeadRotation) > 0.1f)
			{
				if (Time.realtimeSinceStartup <= m_TimeSinceLastHMDChange + k_HMDActivityTimeout)
					SetSceneViewsEnabled(false);

				// Keep track of HMD activity by tracking head rotations
				m_TimeSinceLastHMDChange = Time.realtimeSinceStartup;
			}

			if (m_LatchHMDValues)
			{
				cameraTransform.localPosition = headPosition;
				cameraTransform.localRotation = headRotation;
				if (!m_HMDReady)
				{
					m_HMDReady = true;
					if (onHMDReady != null)
						onHMDReady();
				}
			}

			m_LastHeadRotation = headRotation;
		}

		// TODO: Share this between SceneView/EditorVR in SceneViewUtilies
		public void CreateCameraTargetTexture(ref RenderTexture renderTexture, Rect cameraRect, bool hdr)
		{
			bool useSRGBTarget = QualitySettings.activeColorSpace == ColorSpace.Linear;

			int msaa = Mathf.Max(1, QualitySettings.antiAliasing);

			RenderTextureFormat format = hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
			if (renderTexture != null)
			{
				bool matchingSRGB = renderTexture != null && useSRGBTarget == renderTexture.sRGB;

				if (renderTexture.format != format || renderTexture.antiAliasing != msaa || !matchingSRGB)
				{
					Object.DestroyImmediate(renderTexture);
					renderTexture = null;
				}
			}

			Rect actualCameraRect = cameraRect;
			int width = (int)actualCameraRect.width;
			int height = (int)actualCameraRect.height;

			if (renderTexture == null)
			{
				renderTexture = new RenderTexture(0, 0, 24, format);
				renderTexture.name = "Scene RT";
				renderTexture.antiAliasing = msaa;
				renderTexture.hideFlags = HideFlags.HideAndDontSave;
			}
			if (renderTexture.width != width || renderTexture.height != height)
			{
				renderTexture.Release();
				renderTexture.width = width;
				renderTexture.height = height;
			}
			renderTexture.Create();
		}

		private void PrepareCameraTargetTexture(Rect cameraRect)
		{
			// Always render camera into a RT
			CreateCameraTargetTexture(ref m_SceneTargetTexture, cameraRect, false);
			m_Camera.targetTexture = m_ShowDeviceView ? m_SceneTargetTexture : null;
			VRSettings.showDeviceView = !customPreviewCamera && m_ShowDeviceView;
		}

		private void OnGUI()
		{
			if (onGUIDelegate != null)
				onGUIDelegate(this);

			var e = Event.current;
			if (e.type != EventType.ExecuteCommand && e.type != EventType.used)
			{
				SceneViewUtilities.ResetOnGUIState();

				var guiRect = new Rect(0, 0, position.width, position.height);
				var cameraRect = EditorGUIUtility.PointsToPixels(guiRect);
				PrepareCameraTargetTexture(cameraRect);

				m_Camera.cullingMask = m_CullingMask.HasValue ? m_CullingMask.Value.value : UnityEditor.Tools.visibleLayers;

				// Draw camera
				bool pushedGUIClip;
				DoDrawCamera(guiRect, out pushedGUIClip);

				if (m_ShowDeviceView)
					SceneViewUtilities.DrawTexture(customPreviewCamera && customPreviewCamera.targetTexture ? customPreviewCamera.targetTexture : m_SceneTargetTexture, guiRect, pushedGUIClip);

				GUILayout.BeginArea(guiRect);
				{
					if (GUILayout.Button("Toggle Device View", EditorStyles.toolbarButton))
						m_ShowDeviceView = !m_ShowDeviceView;

					if (m_CustomPreviewCamera)
					{
						GUILayout.FlexibleSpace();
						GUILayout.BeginHorizontal();
						{
							GUILayout.FlexibleSpace();
							m_UseCustomPreviewCamera = GUILayout.Toggle(m_UseCustomPreviewCamera, "Use Presentation Camera");
						}
						GUILayout.EndHorizontal();
					}
				}
				GUILayout.EndArea();
			}
		}

		private void DoDrawCamera(Rect cameraRect, out bool pushedGUIClip)
		{
			pushedGUIClip = false;
			if (!m_Camera.gameObject.activeInHierarchy)
				return;

			if (!m_VRInitialized)
				return;

			SceneViewUtilities.DrawCamera(m_Camera, cameraRect, position, m_RenderMode, out pushedGUIClip);
		}

		private void OnPlaymodeStateChanged()
		{
			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				EditorPrefs.SetBool(k_LaunchOnExitPlaymode, true);
				Close();
			}
		}

		private void Update()
		{
			// If code is compiling, then we need to clean up the window resources before classes get re-initialized
			if (EditorApplication.isCompiling)
			{
				Close();
				return;
			}

			// Force the window to repaint every tick, since we need live updating
			// This also allows scripts with [ExecuteInEditMode] to run
			SceneViewUtilities.SetSceneRepaintDirty();

			UpdateCamera();

			// Re-enable the other scene views if there has been no activity from the HMD (allows editing in SceneView)
			if (Time.realtimeSinceStartup >= m_TimeSinceLastHMDChange + k_HMDActivityTimeout)
				SetSceneViewsEnabled(true);
		}

		private void SetGameViewsEnabled(bool enabled)
		{
			Assembly asm = Assembly.GetAssembly(typeof(UnityEditor.EditorWindow));
			Type type = asm.GetType("UnityEditor.GameView");
			SceneViewUtilities.SetViewsEnabled(type, enabled);
		}

		private void SetSceneViewsEnabled(bool enabled)
		{
			// It's costly to call through to SetViewsEnabled, so only call when the value has changed
			if (m_SceneViewsEnabled != enabled)
			{
				SceneViewUtilities.SetViewsEnabled(typeof(SceneView), enabled);
				m_SceneViewsEnabled = enabled;
			}
		}

		private void SetOtherViewsEnabled(bool enabled)
		{
			SetGameViewsEnabled(enabled);
			SetSceneViewsEnabled(enabled);
		}

		static InputManager s_InputManager;
		public const HideFlags kDefaultHideFlags = HideFlags.DontSave;

		private static void InitializeInputManager()
		{
			// HACK: InputSystem has a static constructor that is relied upon for initializing a bunch of other components, so
			// in edit mode we need to handle lifecycle explicitly
			InputManager[] managers = Resources.FindObjectsOfTypeAll<InputManager>();
			foreach (var m in managers)
			{
				ObjectUtils.Destroy(m.gameObject);
			}

			managers = Resources.FindObjectsOfTypeAll<InputManager>();
			if (managers.Length == 0)
			{
				// Attempt creating object hierarchy via an implicit static constructor call by touching the class
				InputSystem.ExecuteEvents();
				managers = Resources.FindObjectsOfTypeAll<InputManager>();

				if (managers.Length == 0)
				{
					typeof(InputSystem).TypeInitializer.Invoke(null, null);
					managers = Resources.FindObjectsOfTypeAll<InputManager>();
				}
			}
			Assert.IsTrue(managers.Length == 1, "Only one InputManager should be active; Count: " + managers.Length);

			s_InputManager = managers[0];
			s_InputManager.gameObject.hideFlags = kDefaultHideFlags;
			ObjectUtils.SetRunInEditModeRecursively(s_InputManager.gameObject, true);

			// These components were allocating memory every frame and aren't currently used in EditorVR
			ObjectUtils.Destroy(s_InputManager.GetComponent<JoystickInputToEvents>());
			ObjectUtils.Destroy(s_InputManager.GetComponent<MouseInputToEvents>());
			ObjectUtils.Destroy(s_InputManager.GetComponent<KeyboardInputToEvents>());
			ObjectUtils.Destroy(s_InputManager.GetComponent<TouchInputToEvents>());
		}


		/// <summary>
		/// The context stack.  We hold game objects.  But all are expected to have a MonoBehavior that implements IEditingContext.
		/// </summary>
		private List<GameObject> m_ContextStack = new List<GameObject>();

		/// <summary>
		/// Attempt to find and fetch the current context.
		/// </summary>
		/// <param name="current">The current context.  Or null if there is none.</param>
		/// <returns>True if there is a current context.  False otherwise.</returns>
		private bool FindCurrentContext(out GameObject current)
		{
			if (m_ContextStack.Count == 0)
			{
				current = null;
				return false;
			}
			else
			{
				current = m_ContextStack[m_ContextStack.Count - 1];
				return true;
			}
		}

		public GameObject PushEditingContext<T, C>(C config) where T : MonoBehaviour, IEditingContext<C>
		{
			var newContext = PushEditingContext<T>();
			newContext.GetComponent<T>().Configure(config);
			return newContext;
		}

		public GameObject PushEditingContext<T>() where T : MonoBehaviour, IEditingContext
		{
			//if there is a current context, we subvert and deactivate it.
			GameObject previousContext;
			if (FindCurrentContext(out previousContext))
			{
				previousContext.GetComponent<IEditingContext>().OnSubvertContext();
				previousContext.SetActive(false);
			}

			//create the new context and add it to the stack.
			GameObject newContext = ObjectUtils.CreateGameObjectWithComponent<T>().gameObject;
			m_ContextStack.Add(newContext);
			return newContext;
		}

		public void PopEditingContext()
		{
			GameObject poppedContext;
			if (FindCurrentContext(out poppedContext))
			{
				poppedContext.SetActive(false);
				m_ContextStack.RemoveAt(m_ContextStack.Count - 1);
				ObjectUtils.Destroy(poppedContext);
			}
			GameObject revivedContext;
			if (FindCurrentContext(out revivedContext))
			{
				revivedContext.SetActive(true);
				revivedContext.GetComponent<IEditingContext>().OnReviveContext();
			}
		}
	}

}
#endif