# Ditto - Servicio de Simulación de Servicios

Ditto es una herramienta de consola desarrollada en C# que permite simular diferentes tipos de servicios (REST, TCP, SOAP, COM) mediante archivos de configuración.

## Características

- ✅ Servicios REST (implementado)
- ✅ Servicios TCP (implementado)
- ✅ Servicios SOAP (implementado)
- ✅ Servicios COM (implementado)

## Requisitos

- .NET 8.0 SDK o superior

## Uso

### Ejecutar la aplicación

```bash
dotnet run
```

O especificar un archivo de configuración personalizado:

```bash
dotnet run mi-configuracion.json
```

### Archivo de Configuración

El archivo de configuración por defecto es `services.json`. Ejemplo de estructura:

```json
[
  {
    "type": "REST",
    "name": "Nombre del Servicio",
    "port": 8080,
    "endpoints": [
      {
        "path": "/api/users",
        "method": "GET",
        "statusCode": 200,
        "responseBody": {
          "message": "Respuesta JSON"
        },
        "headers": {
          "Content-Type": "application/json"
        },
        "delayMs": 100
      }
    ]
  }
]
```

### Parámetros de Endpoint

#### Para Servicios REST:
- `path`: Ruta del endpoint (ej: `/api/users`)
- `method`: Método HTTP (GET, POST, PUT, DELETE, etc.)
- `statusCode`: Código de estado HTTP a devolver
- `headers`: Diccionario de headers HTTP

#### Para Servicios TCP:
- `pattern`: Patrón regex opcional para matchear mensajes TCP. Si no se especifica, el endpoint se usa como predeterminado cuando no hay otros matches.
  - Puedes usar **grupos de captura nombrados** (ej: `(?<nombreGrupo>patrón)`) para extraer valores del mensaje.
  - Los grupos capturados estarán disponibles en Handlebars como `{{request.captures.nombreGrupo}}`.

#### Para Servicios SOAP:
- `path`: Ruta del endpoint (ej: `/soap/GetUser`)
- `method`: Método HTTP (normalmente POST para SOAP)
- `statusCode`: Código de estado HTTP a devolver
- `headers`: Diccionario de headers HTTP (Content-Type por defecto es `text/xml; charset=utf-8` si no se especifica)

#### Para Servicios COM:
- `pattern`: Patrón regex opcional para matchear mensajes COM. Si no se especifica, el endpoint se usa como predeterminado cuando no hay otros matches.
  - Puedes usar **grupos de captura nombrados** (ej: `(?<nombreGrupo>patrón)`) para extraer valores del mensaje.
  - Los grupos capturados estarán disponibles en Handlebars como `{{request.captures.nombreGrupo}}`.

**Nota:** Los servicios COM usan puertos seriales (COM1, COM2, COM9, etc.) para comunicación serial. El `port` en la configuración debe ser el número del puerto serial (ej: `9` para COM9) o el nombre completo del puerto (ej: `"COM9"`). Los parámetros seriales por defecto son: 9600 baudios, 8 bits de datos, sin paridad, 1 bit de parada.

#### Parámetros Comunes:
- `responseBody`: Objeto JSON que será la respuesta. Soporta expresiones Handlebars para generar respuestas dinámicas basadas en la solicitud. **Mutuamente exclusivo con `responseBodyFilePath`**.
- `responseBodyFilePath`: Ruta a un archivo JSON que contiene el cuerpo de la respuesta. El archivo puede contener expresiones Handlebars. Si la ruta es relativa, se busca desde el directorio de trabajo actual. **Mutuamente exclusivo con `responseBody`**.
- `delayMs`: Delay opcional en milisegundos antes de responder

### Templates Handlebars

El `responseBody` soporta expresiones Handlebars que permiten generar respuestas dinámicas basadas en la solicitud HTTP. El contexto disponible incluye:

- `{{request.method}}`: Método HTTP de la solicitud (GET, POST, etc.)
- `{{request.path}}`: Ruta absoluta de la solicitud
- `{{request.query.param}}`: Parámetros de consulta (ej: `{{request.query.id}}`)
- `{{request.headers.HeaderName}}`: Headers de la solicitud (ej: `{{request.headers.User-Agent}}`)
- `{{request.body}}`: Cuerpo completo de la solicitud (si existe)
- `{{request.body.fieldName}}`: Campos específicos del body JSON (ej: `{{request.body.name}}`, `{{request.body.email}}`)
- `{{#each request.body.array}}...{{/each}}`: Bloque de iteración para recorrer colecciones (ej: `{{#each request.body.users}}{{this.name}}{{/each}}`)

#### Ejemplos Básicos:
```json
{
  "path": "/api/users",
  "method": "GET",
  "responseBody": {
    "message": "Solicitud recibida con método {{request.method}}",
    "path": "{{request.path}}",
    "userId": "{{request.query.id}}"
  }
}
```

### Servicios TCP

Los servicios TCP permiten simular servidores TCP que escuchan conexiones entrantes y responden con datos configurados.

#### Ejemplo de Configuración TCP:

```json
{
  "type": "TCP",
  "name": "Servicio TCP",
  "port": 9999,
  "endpoints": [
    {
      "pattern": ".*GET.*",
      "responseBody": {
        "action": "GET",
        "message": "{{request.message}}",
        "clientAddress": "{{request.clientAddress}}"
      }
    },
    {
      "responseBodyFilePath": "responses/tcp-response.json"
    }
  ]
}
```

#### Contexto Handlebars para TCP:

El contexto disponible en servicios TCP incluye:

- `{{request.message}}`: Mensaje completo recibido como string
- `{{request.parsedMessage}}`: Mensaje parseado como objeto (si es JSON válido) o string
- `{{request.parsedMessage.fieldName}}`: Campos específicos del mensaje parseado si es JSON (ej: `{{request.parsedMessage.name}}`)
- `{{request.clientAddress}}`: Dirección IP del cliente
- `{{request.clientPort}}`: Puerto del cliente
- `{{request.timestamp}}`: Timestamp ISO 8601 de cuando se recibió el mensaje
- `{{request.captures.nombreGrupo}}`: Valor capturado de un grupo nombrado en el patrón regex (ej: `{{request.captures.command}}`, `{{request.captures.key}}`)

Si el mensaje recibido es JSON válido, puedes acceder a sus campos usando `{{request.parsedMessage.fieldName}}`.

#### Extracción de Valores con Regex:

Los servicios TCP soportan extraer valores específicos del mensaje usando **grupos de captura nombrados** en regex. Esto es especialmente útil cuando el protocolo TCP no tiene un formato estándar.

**Ejemplo de configuración con grupos de captura:**

```json
{
  "type": "TCP",
  "name": "Servicio TCP con Capturas",
  "port": 9999,
  "endpoints": [
    {
      "pattern": "CMD:(?<command>\\w+)\\s+KEY:(?<key>\\w+)\\s+VALUE:(?<value>.*)",
      "responseBody": {
        "status": "OK",
        "receivedCommand": "{{request.captures.command}}",
        "key": "{{request.captures.key}}",
        "value": "{{request.captures.value}}",
        "fullMessage": "{{request.message}}"
      }
    }
  ]
}
```

**Ejemplo de mensaje TCP entrante:**
```
CMD:SET KEY:username VALUE:johndoe
```

**Respuesta generada:**
```json
{
  "status": "OK",
  "receivedCommand": "SET",
  "key": "username",
  "value": "johndoe",
  "fullMessage": "CMD:SET KEY:username VALUE:johndoe"
}
```

Los grupos de captura se definen usando la sintaxis `(?<nombreGrupo>patrón)` en el regex. El nombre del grupo debe ser un identificador válido (letras, números, guiones bajos).

### Servicios SOAP

Los servicios SOAP permiten simular servicios web SOAP que escuchan peticiones HTTP y responden con XML (SOAP envelopes).

#### Ejemplo de Configuración SOAP:

```json
{
  "type": "SOAP",
  "name": "Servicio SOAP",
  "port": 8888,
  "endpoints": [
    {
      "path": "/soap/GetUser",
      "method": "POST",
      "statusCode": 200,
      "responseBody": "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><GetUserResponse><UserId>{{request.body.GetUser.UserId}}</UserId><UserName>{{request.body.GetUser.UserName}}</UserName></GetUserResponse></soap:Body></soap:Envelope>",
      "headers": {
        "Content-Type": "text/xml; charset=utf-8"
      }
    },
    {
      "path": "/soap/GetProduct",
      "method": "POST",
      "statusCode": 200,
      "responseBodyFilePath": "responses/soap-response.xml",
      "headers": {
        "Content-Type": "text/xml; charset=utf-8",
        "SOAPAction": "\"GetProduct\""
      }
    }
  ]
}
```

#### Contexto Handlebars para SOAP:

El contexto disponible en servicios SOAP es similar a REST:

- `{{request.method}}`: Método HTTP de la solicitud (normalmente POST)
- `{{request.path}}`: Ruta absoluta de la solicitud
- `{{request.query.param}}`: Parámetros de consulta (ej: `{{request.query.id}}`)
- `{{request.headers.HeaderName}}`: Headers de la solicitud (ej: `{{request.headers.SOAPAction}}`)
- `{{request.body}}`: Cuerpo completo de la solicitud parseado como XML (convertido a diccionario)
- `{{request.body.elementName}}`: Elementos específicos del body XML (ej: `{{request.body.GetUser.UserId}}`)

**Notas importantes sobre SOAP:**
- El `Content-Type` por defecto es `text/xml; charset=utf-8` si no se especifica en los headers.
- Las respuestas SOAP normalmente son XML. El `responseBody` puede ser una cadena XML (que puede contener Handlebars) o un objeto que se serializará como JSON.
- El body de la request XML se parsea automáticamente y se convierte a un diccionario para que Handlebars pueda acceder a los elementos XML.
- Los archivos de respuesta (`responseBodyFilePath`) pueden contener XML con expresiones Handlebars.

### Servicios COM

Los servicios COM permiten simular servicios de comunicación serial usando puertos COM (COM1, COM2, COM9, etc.). Son similares a los servicios TCP pero usan puertos seriales en lugar de sockets de red.

#### Ejemplo de Configuración COM:

```json
{
  "type": "COM",
  "name": "Servicio COM",
  "port": 9,
  "endpoints": [
    {
      "pattern": "CMD:(?<command>\\w+)\\s+KEY:(?<key>\\w+)\\s+VALUE:(?<value>.*)",
      "responseBody": {
        "status": "OK",
        "receivedCommand": "{{request.captures.command}}",
        "key": "{{request.captures.key}}",
        "value": "{{request.captures.value}}",
        "fullMessage": "{{request.message}}"
      }
    },
    {
      "responseBodyFilePath": "responses/com-response.json"
    }
  ]
}
```

#### Contexto Handlebars para COM:

El contexto disponible en servicios COM es similar a TCP pero sin información de cliente (dirección IP/puerto):

- `{{request.message}}`: Mensaje completo recibido como string
- `{{request.parsedMessage}}`: Mensaje parseado como objeto (si es JSON válido) o string
- `{{request.parsedMessage.fieldName}}`: Campos específicos del mensaje parseado si es JSON (ej: `{{request.parsedMessage.name}}`)
- `{{request.timestamp}}`: Timestamp ISO 8601 de cuando se recibió el mensaje
- `{{request.captures.nombreGrupo}}`: Valor capturado de un grupo nombrado en el patrón regex (ej: `{{request.captures.command}}`, `{{request.captures.key}}`)

**Notas importantes sobre COM:**
- Los servicios COM usan puertos seriales (COM1, COM2, COM9, etc.) para comunicación serial.
- El `port` en la configuración debe ser el número del puerto (ej: `9` para COM9) o el nombre completo (ej: `"COM9"`).
- Los parámetros seriales por defecto son: 9600 baudios, 8 bits de datos, sin paridad, 1 bit de parada.
- Los mensajes se procesan cuando se recibe un salto de línea (`\n` o `\r\n`).
- Soporta todas las características de TCP: regex matching, grupos de captura, Handlebars, archivos de respuesta, y delays.
- Los mensajes se pueden parsear como JSON automáticamente si son válidos.

## Arquitectura

El proyecto está diseñado siguiendo los principios SOLID:

- **Single Responsibility**: Cada clase tiene una responsabilidad única
- **Open/Closed**: Extensible para nuevos tipos de servicios sin modificar código existente
- **Liskov Substitution**: Interfaces claras que permiten intercambiabilidad
- **Interface Segregation**: Interfaces específicas y cohesivas
- **Dependency Inversion**: Dependencias hacia abstracciones (interfaces)

### Estructura del Proyecto

```
Ditto/
├── Configuration/        # Cargadores de configuración
├── Interfaces/           # Contratos e interfaces
├── Models/              # Modelos de datos
├── Services/            # Implementaciones de servicios
├── Program.cs           # Punto de entrada
└── services.json        # Archivo de configuración de ejemplo
```

## Detener la Aplicación

Presione `Ctrl+C` para detener todos los servicios de forma ordenada.
