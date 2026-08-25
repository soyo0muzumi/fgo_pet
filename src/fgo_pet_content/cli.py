import typer


app = typer.Typer(help="FGO Pet content pipeline")


@app.callback()
def main() -> None:
    """Extract and package local FGO Pet content."""


if __name__ == "__main__":
    app()
