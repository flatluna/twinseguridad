# TwinSeguridad

Azure Functions (.NET 9) para la extraccion, indexacion y consulta inteligente de normas de seguridad laboral mexicanas, con un **Agente AI conversacional** integrado.

## Descripcion

TwinSeguridad procesa la **NOM-002-STPS-2010** (Prevencion y Proteccion contra Incendios en Centros de Trabajo) y su Manual de Procedimientos. El sistema:

1. **Extrae** el contenido de PDFs (texto, tablas, imagenes)
2. **Estructura** la informacion en secciones e indices con conteo de tokens
3. **Indexa** los documentos en **Azure Cosmos DB** con embeddings vectoriales
4. **Genera training** con preguntas frecuentes y cursos de capacitacion usando GPT-4o-mini
5. **Busca** semanticamente usando `VectorDistance` en Cosmos DB
6. **Conversa** a traves de un Agente AI con memoria de sesion y herramientas MCP

## Tecnologias

| Componente | Tecnologia |
|---|---|
| Runtime | .NET 9 / Azure Functions v4 (Isolated Worker) |
| AI / LLM | Azure OpenAI (GPT-4o-mini, text-embedding-ada-002) |
| Base de datos | Azure Cosmos DB (NoSQL con Vector Search) |
| Agente AI | Microsoft.Agents.AI + Microsoft.Extensions.AI |
| Extraccion PDF | UglyToad.PdfPig |
| Tokenizacion | SharpToken (cl100k_base) |

## Estructura del proyecto

```
TwinSeguridad/
|-- Agent/                          # Agente AI conversacional
|   |-- AgentSeguridadNormaFx.cs    #   Endpoints del agente (chat, sesiones)
|   |-- SeguridadNormaMcpTools.cs   #   Herramientas MCP (busqueda vectorial)
|   +-- SeguridadNormaMemory.cs     #   Memoria / AIContextProvider
|-- Models/                         # Modelos de datos
|   |-- DocumentoLey.cs             #   Documento PDF extraido
|   |-- IndiceNorma.cs              #   Indice jerarquico con tokens
|   |-- NormaEstructurada.cs        #   Norma estructurada (indices + subindices)
|   |-- LeySeguridadVectorDocument.cs   # Documento vectorial en Cosmos DB
|   |-- LeySeguridadTrainingDocument.cs # Documento de training (FAQ + cursos)
|   +-- ManualSeguridadDocument.cs  #   Seccion del manual
|-- Services/                       # Logica de negocio
|   |-- PdfExtractionService.cs     #   Extraccion de PDF
|   |-- IndiceExtractionService.cs  #   Generacion de indice jerarquico
|   |-- TextExportService.cs        #   Exportacion a texto plano
|   |-- NormaEstructuradaService.cs #   Estructuracion con sumarios
|   |-- LeySeguridadCosmosVectorService.cs  # Indexacion vectorial + busqueda
|   |-- LeySeguridadTrainingService.cs      # Generacion de training con AI
|   +-- ManualSeguridadService.cs   #   Extraccion del manual de procedimientos
|-- SeguridadNormaFx.cs            # Endpoints HTTP principales
|-- Program.cs                      # Registro de servicios (DI)
|-- host.json                       # Configuracion de Azure Functions
+-- local.settings.json             # Configuracion local (NO se sube a Git)
```

## Endpoints API

### Extraccion y procesamiento

| Metodo | Ruta | Descripcion |
|---|---|---|
| GET/POST | `/api/seguridad/extraer` | Extrae PDF a JSON con lineas, tablas e imagenes |
| GET/POST | `/api/seguridad/indice` | Genera indice jerarquico con conteo de tokens |
| GET/POST | `/api/seguridad/exportar-texto` | Exporta a un archivo TXT limpio |
| GET/POST | `/api/seguridad/estructurar` | Genera JSON estructurado con sumarios |
| GET/POST | `/api/seguridad/estructurar-texto` | Estructura desde texto plano |

### Indexacion y busqueda vectorial

| Metodo | Ruta | Descripcion |
|---|---|---|
| GET/POST | `/api/seguridad/indexar` | Indexa la norma en Cosmos DB con vectores |
| GET/POST | `/api/seguridad/indexar-ley` | Indexa con chunking (~800 chars) + embeddings |
| POST | `/api/seguridad/buscar-ley` | Busqueda semantica vectorial |

### Training (FAQ + cursos)

| Metodo | Ruta | Descripcion |
|---|---|---|
| GET/POST | `/api/seguridad/generar-training` | Genera preguntas y cursos con GPT-4o-mini |
| GET | `/api/seguridad/training` | Lista todos los documentos de training |
| GET | `/api/seguridad/training/indices` | Lista solo indices y titulos (ligero) |
| GET | `/api/seguridad/training/{indice}` | Un documento de training completo |

### Manual de procedimientos

| Metodo | Ruta | Descripcion |
|---|---|---|
| GET/POST | `/api/seguridad/indexar-manual` | Extrae ManualSeguridad.pdf a Cosmos DB |
| GET | `/api/seguridad/manual` | Lista todas las secciones del manual |
| GET | `/api/seguridad/manual/{indice}` | Una seccion completa del manual |

### Agente AI conversacional

| Metodo | Ruta | Descripcion |
|---|---|---|
| POST | `/api/seguridad/agent/chat` | Chat con el agente (multi-turno) |
| GET | `/api/seguridad/agent/{sessionId}/history` | Info de la sesion |
| DELETE | `/api/seguridad/agent/{sessionId}` | Terminar sesion |
| GET | `/api/seguridad/agent/info` | Info del agente y sesiones activas |

## Configuracion

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

> Este archivo contiene secretos y esta excluido de Git via `.gitignore`.

## Ejecucion local

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

## Licencia

Proyecto privado - TwiNetAI
