namespace Ditto.Interfaces;

public interface ITemplateProcessor
{
    object ProcessTemplate(object template, object context);
}
