"""Point d'entree PyInstaller : delegue au CLI du moteur."""
from transcriber_engine.__main__ import main

if __name__ == "__main__":
    raise SystemExit(main())
