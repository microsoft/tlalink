machine Credit {
    start state Init {
        entry {
            assert ReturnCredit(0) == 1, "credit-return";
        }
    }
    fun ReturnCredit(available: int): int {
        assert available >= 0 && available < 2, "credit-range";
        return available + 1;
    }
}
test tcCredit [main = Credit]: { Credit };
