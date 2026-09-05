using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine; 

public class DelayAnimStart : MonoBehaviour
{
    float wait = 0f; 
    public float DelayAmount = 1f;
    public Animation animator;
    bool waitFinished = false; 

    // Start is called before the first frame update
    void Start()
    {
        wait = 0f;
        animator = this.GetComponent<Animation>(); 
    }

    // Update is called once per frame
    void Update()
    {
        if(waitFinished) return; 
        wait += Time.deltaTime;

        if(wait > DelayAmount)
        {
            animator.Play();
            waitFinished = true; 
            //DelayAmount = 1000000000; 
		}
    }
}
