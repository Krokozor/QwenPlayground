namespace QwenPlayground.Core.Templates {
    /// <summary>
    /// Единый источник служебных токенов шаблона Qwen. Public, чтобы тесты и другие
    /// слои брали токены отсюда, а не дублировали литералы (риск опечатки/эранирования).
    /// </summary>
    public static class QwenSpecialTokens {
        /// <summary>Открывает ход: "&lt;|im_start|&gt;{role}\n...". Общий для всех ролей.</summary>
        public const string ImStart = "<|im_start|>";

        /// <summary>Закрывает текущий ход.</summary>
        public const string ImEnd = "<|im_end|>";

        /// <summary>Токен конца текста — stop-токен инференса (дополняет ImEnd).</summary>
        public const string EndOfText = "<|endoftext|>";

        /// <summary>Открывающая граница визуального блока (общая для картинок и видео).</summary>
        public const string VisionStart = "<|vision_start|>";

        /// <summary>Закрывающая граница визуального блока.</summary>
        public const string VisionEnd = "<|vision_end|>";

        /// <summary>Плейсхолдер одного изображения между VisionStart и VisionEnd.</summary>
        public const string ImagePad = "<|image_pad|>";

        /// <summary>Плейсхолдер одного видео между VisionStart и VisionEnd.</summary>
        public const string VideoPad = "<|video_pad|>";

        /// <summary>Начало блока рассуждений (сразу после роли assistant).</summary>
        public const string ThinkStart = "<think>";

        /// <summary>Конец блока рассуждений, за которым следует видимый ответ.</summary>
        public const string ThinkEnd = "</think>";

        /// <summary>Открывает список доступных функций в системном промпте.</summary>
        public const string ToolsListStart = "<tools>";

        /// <summary>Закрывает список доступных функций.</summary>
        public const string ToolsListEnd = "</tools>";

        /// <summary>Открывает блок вызова функции моделью.</summary>
        public const string ToolCallStart = "<tool_call>";

        /// <summary>Закрывает блок вызова функции.</summary>
        public const string ToolCallEnd = "</tool_call>";

        /// <summary>Открывает результат выполнения инструмента (передаётся обратно модели).</summary>
        public const string ToolResponseStart = "<tool_response>";

        /// <summary>Закрывает результат выполнения инструмента.</summary>
        public const string ToolResponseEnd = "</tool_response>";

        /// <summary>Закрывающий тег вызываемой функции.</summary>
        public const string FunctionEnd = "</function>";

        /// <summary>Закрывающий тег параметра функции.</summary>
        public const string ParameterEnd = "</parameter>";

        /// <summary>
        /// Формат открывающего тега функции — содержит имя функции, поэтому не может быть
        /// готовой константой-значением целиком. В шаблоне: "&lt;function=example_function_name&gt;".
        /// Используйте string.Format(FunctionStartFormat, name) или метод FunctionStart(name).
        /// </summary>
        public const string FunctionStartFormat = "<function={0}>";

        /// <summary>Префикс тега функции без имени — "&lt;function=". Для поиска/разбора вывода.</summary>
        public const string FunctionStartPrefix = "<function=";

        /// <summary>
        /// Формат открывающего тега параметра — содержит имя параметра.
        /// В шаблоне: "&lt;parameter=example_parameter_1&gt;".
        /// Используйте string.Format(ParameterStartFormat, name) или метод ParameterStart(name).
        /// </summary>
        public const string ParameterStartFormat = "<parameter={0}>";

        /// <summary>Префикс тега параметра без имени — "&lt;parameter=". Для поиска/разбора вывода.</summary>
        public const string ParameterStartPrefix = "<parameter=";

        /// <summary>Открывает блок важных напоминаний/инструкций в системном промпте.</summary>
        public const string ImportantStart = "<IMPORTANT>";

        /// <summary>Закрывает блок важных напоминаний/инструкций.</summary>
        public const string ImportantEnd = "</IMPORTANT>";

        /// <summary>Собирает "&lt;function=name&gt;" из имени функции.</summary>
        public static string FunctionStart(string functionName) =>
            string.Format(FunctionStartFormat, functionName);

        /// <summary>Собирает "&lt;parameter=name&gt;" из имени параметра.</summary>
        public static string ParameterStart(string parameterName) =>
            string.Format(ParameterStartFormat, parameterName);

        // Значения message.role из шаблона. Формально не токены, но нужны вместе с ImStart
        // для сборки заголовка хода, например: ImStart + Roles.User + "\n".
        public const string System = "system";
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string Tool = "tool";
    }
}
