namespace PExample;

public static class Credit
{
    /* @link {"bind":[{"id":"credit-return","target":"credit-p::Credit.ReturnCredit"}]} */
    public static int ReturnCredit(int available)
    {
        if (available < 0 || available >= 2) throw new System.ArgumentOutOfRangeException(nameof(available));
        return available + 1;
    }
}
