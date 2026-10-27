
internal sealed class Demo
{
    private readonly object gate = new object();

    /* @link {"bind":[{"id":"active","target":"demo::ObservedActive"}],"capture":[{"binding":"active","guard":"this.gate","type":"int"}]} */
    private int active;

    internal void Open()
    {
        lock (this.gate)
        {
            /* @link {"bind":[{"id":"Admit","target":"demo::Admit","reason":"lock"}],"capture":[{"binding":"Admit","actor":"1","operation":"1","point":"commit","classification":"transition","observe":["active"]}]} */
            this.active++;
        }
    }

    internal void Release()
    {
        lock (this.gate)
        {
            /* @link {"bind":[{"id":"Release","target":"demo::Release","reason":"lock"}],"capture":[{"binding":"Release","actor":"1","operation":"1","point":"commit","classification":"transition","observe":["active"]}]} */
            this.active--;
        }
    }
}
