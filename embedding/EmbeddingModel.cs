using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VectorDataBase.Interfaces;

namespace VectorDataBase.Embedding;

public class EmbeddingModel : IEmbeddingModel
{
    private readonly InferenceSession _onnxSession;
    private readonly E5SmallTokenizer _tokenizer;
    private const string RelativePath = "MLModels/e5-small-v2/model.onnx";
    private string _modelPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativePath);

    public EmbeddingModel()
    {
        var options = new SessionOptions();

        // Optimize for your 1070 Ti
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.EnableMemoryPattern = true;

        try
        {
            options.AppendExecutionProvider_CUDA(0);
        }
        catch
        {
            try { options.AppendExecutionProvider_DML(0); }
            catch { Console.WriteLine("[Status] Using CPU fallback."); }
        }

        _onnxSession = new InferenceSession(_modelPath, options);
        _tokenizer = new E5SmallTokenizer("MLModels/e5-small-v2/vocab.txt");
    }

    public float[][] GetEmbeddings(string[] text, bool isQuery)
    {
        // 1. Tokenize (CPU Bound)
        var (tokenIds, tokenTypeIds, attentionMask, batchMaxLen) = _tokenizer.EncodeBatchFlat(text, isQuery);
        int batchSize = text.Length;
        int[] tensorShape = { batchSize, batchMaxLen };

        // 2. Prepare Inputs
        // We wrap these in a list of NamedOnnxValue. 
        // Note: In 1.24.4, DenseTensor<long> is the standard expectation for BERT-style models.
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(tokenIds, tensorShape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, tensorShape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, tensorShape))
        };

        // 3. Run Inference (GPU Bound)
        using var results = _onnxSession.Run(inputs);

        // 4. Extract Result
        // last_hidden_state is index 0 for e5-small
        var outputTensor = results.First().AsTensor<float>();

        // 5. Mean Pooling (CPU Bound)
        return BatchMeanPool(outputTensor, attentionMask, batchSize, batchMaxLen, 384);
    }

    private static float[][] BatchMeanPool(Tensor<float> output, long[] flatMask, int batchSize, int seqLen, int dim)
    {
        float[][] batchVectors = new float[batchSize][];

        for (int i = 0; i < batchSize; i++)
        {
            float[] pooledVector = new float[dim];
            int validTokens = 0;

            for (int j = 0; j < seqLen; j++)
            {
                // Skip padding tokens (0)
                if (flatMask[i * seqLen + j] == 0) continue;

                validTokens++;
                for (int k = 0; k < dim; k++)
                {
                    pooledVector[k] += output[i, j, k];
                }
            }

            // Avoid division by zero and normalize the vector
            float scale = validTokens > 0 ? 1.0f / validTokens : 1.0f;
            for (int k = 0; k < dim; k++)
            {
                pooledVector[k] *= scale;
            }

            batchVectors[i] = pooledVector;
        }

        return batchVectors;
    }

    public EmbeddingModel Factory() => new EmbeddingModel();
}