namespace PExample;

public static class Credit
{
    public static int ReturnCredit(int available)
    {
        if (available < 0 || available >= 2) throw new System.ArgumentOutOfRangeException(nameof(available));
        return available + 1;
    }
}
