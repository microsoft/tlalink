/* @link
model demo;
*/
internal sealed class Demo
{
    private readonly object gate = new object();

    /* @link
    state active guard="this.gate" type=int;
    */
    private int active;

    internal void Open()
    {
        lock (this.gate)
        {
            /* @link
            step Admit justification=lock actor="1" operation="1" point=commit classification=transition observe=active;
            */
            this.active++;
        }
    }

    internal void Release()
    {
        lock (this.gate)
        {
            /* @link
            step Release justification=lock actor="1" operation="1" point=commit classification=transition observe=active;
            */
            this.active--;
        }
    }
}
