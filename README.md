# ??? TwinSeguridad

Azure Functions (.NET 9) para la extracción, indexación y consulta inteligente de normas de seguridad laboral mexicanas, con un **Agente AI conversacional** integrado.

## ?? Descripción

TwinSeguridad procesa la **NOM-002-STPS-2010** (Prevención y Protección contra Incendios en Centros de Trabajo) y su Manual de Procedimientos. El sistema:

1. **Extrae** el contenido de PDFs (texto, tablas, imágenes)
2. **Estructura** la información en secciones e índices con conteo de tokens
3. **Indexa** los documentos en **Azure Cosmos DB** con embeddings vectoriales
4. **Genera training** con preguntas frecuentes y cursos de capacitación usando GPT-4o-mini
5. **Busca** semánticamente usando `VectorDistance` en Cosmos DB
6. **Conversa** a través de un Agente AI con memoria de sesión y herramientas MCP

## ??? Tecnologías

| Componente | Tecnología |
|---|---|
| Runtime | .NET 9 / Azure Functions v4 (Isolated Worker) |
| AI / LLM | Azure OpenAI (GPT-4o-mini, text-embedding-ada-002) |
| Base de datos | Azure Cosmos DB (NoSQL con Vector Search) |
| Agente AI | Microsoft.Agents.AI + Microsoft.Extensions.AI |
| Extracción PDF | UglyToad.PdfPig |
| Tokenización | SharpToken (cl100k_base) |

## ?? Estructura del proyecto

```
TwinSeguridad/
??? Agent/                          # Agente AI conversacional
?   ??? AgentSeguridadNormaFx.cs    #   Endpoints del agente (chat, sesiones)
?   ??? SeguridadNormaMcpTools.cs   #   Herramientas MCP (búsqueda vectorial)
?   ??? SeguridadNormaMemory.cs     #   Memoria / AIContextProvider
??? Models/                         # Modelos de datos
?   ??? DocumentoLey.cs             #   Documento PDF extraído
?   ??? IndiceNorma.cs              #   Índice jerárquico con tokens
?   ??? NormaEstructurada.cs        #   Norma estructurada (índices + subíndices)
?   ??? LeySeguridadVectorDocument.cs   # Documento vectorial en Cosmos DB
?   ??? LeySeguridadTrainingDocument.cs # Documento de training (FAQ + cursos)
?   ??? ManualSeguridadDocument.cs  #   Sección del manual
?   ??? ...
??? Services/                       # Lógica de negocio
?   ??? PdfExtractionService.cs     #   Extracción de PDF
?   ??? IndiceExtractionService.cs  #   Generación de índice jerárquico
?   ??? TextExportService.cs        #   Exportación a texto plano
?   ??? NormaEstructuradaService.cs #   Estructuración con sumarios
?   ??? LeySeguridadCosmosVectorService.cs  # Indexación vectorial + búsqueda
?   ??? LeySeguridadTrainingService.cs      # Generación de training con AI
?   ??? ManualSeguridadService.cs   #   Extracción del manual de procedimientos
?   ??? ...
??? SeguridadNormaFx.cs            # Endpoints HTTP principales
??? Program.cs                      # Registro de servicios (DI)
??? host.json                       # Configuración de Azure Functions
??? local.settings.json             # ?? Configuración local (NO se sube a Git)
```

## ?? Endpoints API

### Extracción y procesamiento

| Método | Ruta | Descripción |
|---|---|---|
| GET/POST | `/api/seguridad/extraer` | Extrae PDF ? JSON con líneas, tablas e imágenes |
| GET/POST | `/api/seguridad/indice` | Genera índice jerárquico con conteo de tokens |
| GET/POST | `/api/seguridad/exportar-texto` | Exporta a un archivo TXT limpio |
| GET/POST | `/api/seguridad/estructurar` | Genera JSON estructurado con sumarios |
| GET/POST | `/api/seguridad/estructurar-texto` | Estructura desde texto plano |

### Indexación y búsqueda vectorial

| Método | Ruta | Descripción |
|---|---|---|
| GET/POST | `/api/seguridad/indexar` | Indexa la norma en Cosmos DB con vectores |
| GET/POST | `/api/seguridad/indexar-ley` | Indexa con chunking (~800 chars) + embeddings |
| POST | `/api/seguridad/buscar-ley` | Búsqueda semántica vectorial |

### Training (FAQ + cursos)

| Método | Ruta | Descripción |
|---|---|---|
| GET/POST | `/api/seguridad/generar-training` | Genera preguntas y cursos con GPT-4o-mini |
| GET | `/api/seguridad/training` | Lista todos los documentos de training |
| GET | `/api/seguridad/training/indices` | Lista solo índices y títulos (ligero) |
| GET | `/api/seguridad/training/{indice}` | Un documento de training completo |

### Manual de procedimientos

| Método | Ruta | Descripción |
|---|---|---|
| GET/POST | `/api/seguridad/indexar-manual` | Extrae ManualSeguridad.pdf ? Cosmos DB |
| GET | `/api/seguridad/manual` | Lista todas las secciones del manual |
| GET | `/api/seguridad/manual/{indice}` | Una sección completa del manual |

### Agente AI conversacional

| Método | Ruta | Descripción |
|---|---|---|
| POST | `/api/seguridad/agent/chat` | Chat con el agente (multi-turno) |
| GET | `/api/seguridad/agent/{sessionId}/history` | Info de la sesión |
| DELETE | `/api/seguridad/agent/{sessionId}` | Terminar sesión |
| GET | `/api/seguridad/agent/info` | Info del agente y sesiones activas |

## ?? Configuración

Crea un archivo `local.settings.json` con las siguientes claves:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",

    "SEGURIDAD_COSMOS_ENDPOINT": "https://<tu-cuenta>.documents.azure.com:443/",
    "SEGURIDAD_COSMOS_KEY": "<tu-key>",
    "SEGURIDAD_COSMOS_DATABASE": "leyesdeseguridaddb",
    "SEGURIDAD_COSMOS_CONTAINER": "leyesseguridadcontainer",

    "AZURE_OPENAI_ENDPOINT": "https://<tu-recurso>.openai.azure.com/",
    "AZURE_OPENAI_API_KEY": "<tu-api-key>",
    "AZURE_OPENAI_EMBEDDING_DEPLOYMENT_NAME": "text-embedding-ada-002",
    "AZURE_OPENAI_CHAT_DEPLOYMENT_NAME": "gpt-4o-mini"
  }
}
```

> ?? Este archivo contiene secretos y está excluido de Git via `.gitignore`.

## ?? Ejecución local

```bash
# Restaurar paquetes
dotnet restore

# Compilar
dotnet build

# Ejecutar
func start
```

Requisitos:
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- Cuenta de Azure Cosmos DB con Vector Search habilitado
- Recurso de Azure OpenAI con los modelos desplegados

## ?? Licencia

Proyecto privado — © TwiNetAI
