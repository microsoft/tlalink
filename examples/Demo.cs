
internal sealed class Demo
{
    private readonly object gate = new object();

    /* @link {"bind":[{"id":"demo::active"}],"capture":[{"binding":"demo::active","guard":"this.gate","type":"int"}]} */
    private int active;

    internal void Open()
    {
        lock (this.gate)
        {
            /* @link {"bind":[{"id":"demo::Admit","reason":"lock"}],"capture":[{"binding":"demo::Admit","actor":"1","operation":"1","point":"commit","classification":"transition","observe":["demo::active"]}]} */
            this.active++;
        }
    }

    internal void Release()
    {
        lock (this.gate)
        {
            /* @link {"bind":[{"id":"demo::Release","reason":"lock"}],"capture":[{"binding":"demo::Release","actor":"1","operation":"1","point":"commit","classification":"transition","observe":["demo::active"]}]} */
            this.active--;
        }
    }
}
