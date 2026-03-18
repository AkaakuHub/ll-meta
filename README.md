[![Platform](https://img.shields.io/badge/platform-Windows-blue)](https://github.com/AkaakuHub/refined-line)

[日本語READMEはこちら](./docs/ja/README.md)

# LLMeta

The client for Linkura VR

# Disclaimer

All code and resources included in this repository are published solely for developers' learning and reference purposes. The author makes no warranties of any kind regarding the accuracy, completeness, or fitness of this code or these resources for any particular purpose. The author shall bear no responsibility whatsoever for any direct or indirect consequences, damages, losses, or legal liabilities arising from the use of this code. All risks associated with its use are to be borne solely by the user.

# Architecture Diagram
```mermaid
flowchart LR
  subgraph W["Windows"]
    direction LR

    subgraph E["Android Emulator"]
      direction TB
      LK["Linkura"]
    end

    subgraph L["LLMeta"]
      direction TB
      BI["Bypass Input"]
      SC["Screen Capture"]
    end

    BI <--> |"TCP transport"| LK
    BI -.-> |"Input forwarding"| LK
    SC --> |"Capture Emulator SBS"| E
  end

  subgraph Q["Meta Quest 3"]
    direction TB
    QI["Input"]
    QO["Output"]
  end

  QI --> |"Controller / Head Tracking"| BI
  SC --> |"OpenXR Render Path"| QO
```

# Contribution

This project is currently in the alpha stage, so contributions are welcome!

# Development Environment

- Windows 11 26H2
- Visual Studio Code
- Meta Quest 3

# Special Thanks

- [linkura-localify](https://github.com/ChocoLZS/linkura-localify)
