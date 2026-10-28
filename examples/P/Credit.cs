namespace PExample;

public static class Credit
{
    /* @link {"bind":[{"id":"credit-p::credit-return"}]} */
    public static int ReturnCredit(int available)
    {
        if (available < 0 || available >= 2) throw new System.ArgumentOutOfRangeException(nameof(available));
        return available + 1;
    }
}
