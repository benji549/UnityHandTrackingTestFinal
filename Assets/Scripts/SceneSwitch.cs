using Unity.VectorGraphics;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneSwitch : MonoBehaviour
{
   public string NextScene;
   public void SwitchScene()
    {
        if(NextScene!= null)
        {
            SceneManager.LoadScene(NextScene, LoadSceneMode.Single);
        }
    }
}
