class Datapoint
{
    public string metric { get; set; }
    public double value { get; set; }
    public string source { get; set; }

    public Datapoint(string metric, double value, string source)
    {
        this.metric = metric;
        this.value = value;
        this.source = source;
    }
}