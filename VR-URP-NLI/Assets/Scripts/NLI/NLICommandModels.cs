using System;

[Serializable]
public class NliCommandEnvelope
{
    public string assistant_text;
    public NliAction[] actions;
}

[Serializable]
public class NliAction
{
    public string type;
    public string value;
    public string target;
    public string label;
    public string color;
    public float fvalue;
    public int ivalue;
    public float strength;
    public bool enable;
}