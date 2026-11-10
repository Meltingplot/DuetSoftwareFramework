using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Standard direct-connect display screen
/// </summary>
[JsonDerivedType(typeof(DirectDisplayScreenST7567))]
public partial class DirectDisplayScreen : ModelObject, IDynamicModelObject
{
    /// <summary>
    /// Number of colour bits
    /// </summary>
    public int ColourBits
    {
        get => _colourBits;
        set => SetPropertyValue(ref _colourBits, value);
    }
    private int _colourBits = 1;

    /// <summary>
    /// Display type
    /// </summary>
    public DirectDisplayController Controller
    {
        get => _controller;
        set => SetPropertyValue(ref _controller, value);
    }
    private DirectDisplayController _controller = DirectDisplayController.ST7920;

    /// <summary>
    /// Height of the display screen in pixels
    /// </summary>
    public int Height
    {
        get => _height;
        set => SetPropertyValue(ref _height, value);
    }
    private int _height = 64;

    /// <summary>
    /// SPI frequency of the display (in Hz)
    /// </summary>
    public int SpiFreq
    {
        get => _spiFreq;
        set => SetPropertyValue(ref _spiFreq, value);
    }
    private int _spiFreq;

    /// <summary>
    /// Width of the display screen in pixels
    /// </summary>
    public int Width
    {
        get => _width;
        set => SetPropertyValue(ref _width, value);
    }
    private int _width = 128;

    /// <summary>
    /// Read the controller of a screen as the <see cref="Controller"/> property reads it, which also takes another case and the number of the value.
    /// The class is chosen by this value, so that the class and the property never disagree
    /// </summary>
    /// <param name="value">Value of the controller</param>
    /// <returns>Controller or null if it is none</returns>
    private static DirectDisplayController? ReadController(JsonElement value)
    {
        try
        {
            return value.Deserialize(ObjectModelContext.Default.DirectDisplayController);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Read the controller of a screen as the <see cref="Controller"/> property reads it
    /// </summary>
    /// <param name="reader">Reader at the value of the controller</param>
    /// <returns>Controller or null if it is none</returns>
    private static DirectDisplayController? ReadController(ref Utf8JsonReader reader)
    {
        try
        {
            return JsonSerializer.Deserialize(ref reader, ObjectModelContext.Default.DirectDisplayController);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Update this instance from a given JSON element
    /// </summary>
    /// <param name="jsonElement">Element to update this intance from</param>
    /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
    /// <returns>Updated instance</returns>
    /// <exception cref="JsonException">Failed to deserialize data</exception>
    public IDynamicModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
    {
        if (jsonElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (jsonElement.TryGetProperty("controller", out JsonElement typeProperty))
        {
            if (ReadController(typeProperty) is DirectDisplayController.ST7567)
            {
                if (this is not DirectDisplayScreenST7567)
                {
                    DirectDisplayScreenST7567 newInstance = new();
                    return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            else if (this is DirectDisplayScreenST7567)
            {
                DirectDisplayScreen newInstance = new();
                return newInstance.UpdateFromJson(jsonElement, ignoreSbcProperties);
            }
        }
        return GeneratedUpdateFromJson(jsonElement, ignoreSbcProperties);
    }

    /// <summary>
    /// Update this instance from a given JSON reader
    /// </summary>
    /// <param name="reader">JSON reader</param>
    /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
    /// <param name="scope">Extent of this update</param>
    /// <returns>Updated instance</returns>
    /// <exception cref="JsonException">Failed to deserialize data</exception>

    public IDynamicModelObject? UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties, ModelUpdateScope scope = ModelUpdateScope.Patch)
    {
        if (reader.TokenType == JsonTokenType.None && !reader.Read())
        {
            throw new JsonException("failed to read from JSON reader");
        }
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("expected start of object");
        }

        // The controller decides the class. If the object has more than one, the last one counts, as in UpdateFromJson. Acting on the first one that
        // asks for another class let two different ones hand the reader back and forth between two instances until the stack overflowed
        Utf8JsonReader readerCopy = reader, keyReader = default;
        bool hasKey = false;
        while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndObject)
        {
            if (readerCopy.TokenType == JsonTokenType.PropertyName)
            {
                if (readerCopy.ValueTextEquals("controller"u8) && readerCopy.Read())
                {
                    keyReader = readerCopy;
                    hasKey = true;
                }
                readerCopy.Skip();
            }
        }

        if (hasKey)
        {
            if (ReadController(ref keyReader) is DirectDisplayController.ST7567)
            {
                if (this is not DirectDisplayScreenST7567)
                {
                    DirectDisplayScreenST7567 newInstance = new();
                    return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
                }
            }
            else if (this is DirectDisplayScreenST7567)
            {
                DirectDisplayScreen newInstance = new();
                return newInstance.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
            }
        }
        return GeneratedUpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
    }
}
