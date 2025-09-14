using UnityEngine;

public class FocusTarget : MonoBehaviour
{
    [Range(0f, 5f)] public float weight = 1f;   // optional: higher = more attractive
    public Vector3 focusOffset = Vector3.zero;  // e.g., fish head offset
}
