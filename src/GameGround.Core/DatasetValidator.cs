namespace GameGround.Core;

public static class DatasetValidator
{
    public static void Validate(MatchDataset dataset) => DatasetEvaluator.Evaluate(dataset);
}
