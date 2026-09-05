using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneDebugger : MonoBehaviour
{
	void Start()
	{
		var scene = SceneManager.GetActiveScene();
		Debug.Log($"Scene name: {scene.name}");
		Debug.Log($"Scene path: {scene.path}");
		Debug.Log($"Root object count: {scene.rootCount}");
		Debug.Log($"Scene loaded: {scene.isLoaded}");
		Debug.Log($"Scene valid: {scene.IsValid()}");

		var allObjects = FindObjectsOfType<GameObject>();
		Debug.Log($"Total GameObjects found: {allObjects.Length}");
	}
}
